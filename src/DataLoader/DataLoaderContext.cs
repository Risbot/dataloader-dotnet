﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataLoader
{
    /// <summary>
    /// Defines a context for creating and executing <see cref="DataLoader{TKey,TReturn}"/> instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class contains any data required by <see cref="DataLoader{TKey,TReturn}"/> instances and is responsible for managing their execution.
    /// </para>
    /// <para>
    /// Loaders enlist themselves with the context active at the time when the <see cref="DataLoader{TKey,TReturn}.LoadAsync"/> method is called.
    /// Later, when the context is triggered (using the <see cref="Execute"/> method), the queue will be processed and each loader executed
    /// in the order they were enlisted.
    /// </para>
    /// <para>
    /// The context should wait until each loader has fetched its data and any continuations have run, before moving on to the next loader.
    /// This allows for keys to be collected from continuation code and also fetched by subsequent loaders as batches.
    /// </para>
    /// </remarks>
    public sealed class DataLoaderContext : IDisposable
    {
        private ConcurrentQueue<Func<Task<Task>>> _queue = new ConcurrentQueue<Func<Task<Task>>>();
        private readonly DataLoaderFactory _factory;

        /// <summary>
        /// Creates a new instance of a context.
        /// </summary>
        /// <remarks>
        /// Reserved for internal use only - consumers should use the static <see cref="Run"/> method.
        /// </remarks>
        internal DataLoaderContext()
        {
            _factory = new DataLoaderFactory(this);
        }

        /// <summary>
        /// Provides methods for obtaining loader instances in this context.
        /// </summary>
        public DataLoaderFactory Factory => _factory;

        /// <summary>
        /// Attaches a delegate to the end of the task chain.
        /// </summary>
        internal void SetNext(Func<Task<Task>> func) => _queue.Enqueue(func);

        /// <summary>
        /// Asynchronously executes loaders until there are none remaining.
        /// </summary>
        /// <remarks>
        /// Loaders will fetch exclusively (i.e. one at a time) but complete concurrently. This allows us
        /// to execute the results efficiently and avoids hitting the DB with multiple parallel requests,
        /// which usually hurts performance.
        /// </remarks>
        internal async void Execute()
        {
            if (_queue.Count == 0) return;

            var tasks = new List<Task>();
            while (_queue.Count > 0 || tasks.Count > 0)
            {
                if (_queue.Count > 0)
                {
                    var localQueue = Interlocked.Exchange(ref _queue, new ConcurrentQueue<Func<Task<Task>>>());
                    while (localQueue.TryDequeue(out var func))
                    {
                        var completion = await func().ConfigureAwait(false);
                        tasks.Add(completion);
                    }
                }

                var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(completed);
            }
        }

        /// <summary>
        /// Indicates whether this context has been disposed.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Disposes of the context. No further loaders/continuations will be attached.
        /// </summary>
        public void Dispose() => IsDisposed = !IsDisposed ? true : throw new ObjectDisposedException(GetType().Name);

#region Ambient Context

        private static readonly AsyncLocal<DataLoaderContext> _localContext = new AsyncLocal<DataLoaderContext>();

        /// <summary>
        /// Represents the ambient context governing the current load operation.
        /// <seealso cref="Run{Task{T}}(Func{Task{T}})"/>
        /// </summary>
        public static DataLoaderContext Current => _localContext.Value;

        /// <summary>
        /// Sets the currently visible ambient loader context.
        /// </summary>
        /// <remarks>
        /// If available, <see cref="DataLoader"/> instances that are not explicitly bound to a context
        /// will register themselves with the ambient context when the load method is called and the
        /// batch is empty.
        /// </remarks>
        internal static void SetLoaderContext(DataLoaderContext context) => _localContext.Value = context;

#endregion

#region Static `Run` Method

        /// <summary>
        /// Runs code within a new loader context before firing any pending loaders.
        /// </summary>
        public static Task<T> Run<T>(Func<Task<T>> func) => Run(_ => func());

        /// <summary>
        /// Runs code within a new loader context before firing any pending loaders.
        /// </summary>
        public static Task Run(Func<Task> func) => Run(_ => func());

        /// <summary>
        /// Runs code within a new loader context before firing any pending loaders.
        /// </summary>
        public static async Task<T> Run<T>(Func<DataLoaderContext, Task<T>> func)
        {
            if (func == null) throw new ArgumentNullException();

            using (var loadCtx = new DataLoaderContext())
            using (new DataLoaderContextSwitcher(loadCtx))
            {
                var task = func(loadCtx);
                loadCtx.Execute();
                return await task.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Runs code within a new loader context before firing any pending loaders.
        /// </summary>
        public static async Task Run(Func<DataLoaderContext, Task> func)
        {
            if (func == null) throw new ArgumentNullException();

            using (var loadCtx = new DataLoaderContext())
            using (new DataLoaderContextSwitcher(loadCtx))
            {
                var task = func(loadCtx);
                loadCtx.Execute();
                await task.ConfigureAwait(false);
            }
        }

#endregion

    }

    /// <summary>
    /// Temporarily switches out the current DataLoaderContext until disposed.
    /// </summary>
    internal class DataLoaderContextSwitcher : IDisposable
    {
        private readonly DataLoaderContext _prevLoadCtx;

        public DataLoaderContextSwitcher(DataLoaderContext loadCtx)
        {
            _prevLoadCtx = DataLoaderContext.Current;
            DataLoaderContext.SetLoaderContext(loadCtx);
        }

        public void Dispose()
        {
            Console.WriteLine($"Thread {Thread.CurrentThread.ManagedThreadId.ToString().PadLeft(2, ' ')} / Task {Task.CurrentId.ToString().PadLeft(2, ' ')} - Disposing of ambient context");
            DataLoaderContext.SetLoaderContext(_prevLoadCtx);
        }
    }
}
