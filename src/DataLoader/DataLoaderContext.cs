﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataLoader
{
    /// <summary>
    /// Defines a context for <see cref="DataLoader"/> instances.
    /// </summary>
    /// <remarks>
    /// This class contains any data required by <see cref="DataLoader"/> instances and is responsible for managing their execution.
    ///
    /// Loaders enlist themselves with the context active at the time when a <code>Load</code> method is called on a loader instance.
    /// When the <see cref="CompleteAsync"/> method is called on the context, it begins executing the enlisted loaders.
    /// Loaders are executed serially, since parallel requests to a database are generally not conducive to good performance or throughput.
    ///
    /// The context will try to wait until each loader - as well as continuations attached to each promise it hands out - finish executing
    /// before moving on to the next. The purpose of this is to allow loaders to enlist or reenlist themselves so that they too are processed
    /// as part the context's completion.
    /// </remarks>
    public sealed class DataLoaderContext
    {
        private readonly object _lock = new object();
        private readonly ConcurrentQueue<IDataLoader> _loaderQueue = new ConcurrentQueue<IDataLoader>();
        private readonly ConcurrentDictionary<object, IDataLoader> _cache = new ConcurrentDictionary<object, IDataLoader>();
        private TaskCompletionSource<object> _completionSource = new TaskCompletionSource<object>();
        private bool _isCompleting;

        internal DataLoaderContext()
        {
        }

        /// <summary>
        /// Retrieves a cached loader for the given key, creating one if none is found.
        /// </summary>
        public IDataLoader<TKey, TReturn> GetLoader<TKey, TReturn>(object key, Func<IEnumerable<TKey>, Task<ILookup<TKey, TReturn>>> fetcher)
        {
            return (IDataLoader<TKey, TReturn>)_cache.GetOrAdd(key, _ => new DataLoader<TKey, TReturn>(fetcher, this));
        }

        /// <summary>
        /// Begins processing the waiting loaders, firing them sequentially until there are none remaining.
        /// </summary>
        /// <remarks>
        /// Loaders are fired in the order that they are first called. Once completed the context cannot be reused.
        /// </remarks>
        public async Task CompleteAsync()
        {
            lock (_lock)
            {
                if (_isCompleting) throw new InvalidOperationException();
                _isCompleting = true;
            }

            while (_loaderQueue.TryDequeue(out IDataLoader loader))
            {
                await loader.ExecuteAsync().ConfigureAwait(false);
            }

            _isCompleting = false;
        }

        /// <summary>
        /// Queues a loader to be executed.
        /// </summary>
        internal void AddToQueue(IDataLoader loader)
        {
            _loaderQueue.Enqueue(loader);
        }

#if !FEATURE_ASYNCLOCAL

        // No-ops for .NET 4.5 (so we don't have to change the remaining codebase)
        internal static DataLoaderContext Current => null;
        internal static void SetCurrentContext(DataLoaderContext context) {}

#else

        private static readonly AsyncLocal<DataLoaderContext> LocalContext = new AsyncLocal<DataLoaderContext>();

        /// <summary>
        /// Represents ambient data local to the current load operation.
        /// <seealso cref="DataLoaderContext.Run{T}(Func{T}})"/>
        /// </summary>
        public static DataLoaderContext Current => LocalContext.Value;

        /// <summary>
        /// Sets the <see cref="DataLoaderContext"/> visible from the <see cref="DataLoaderContext.Current"/>  property.
        /// </summary>
        /// <param name="context"></param>
        internal static void SetCurrentContext(DataLoaderContext context)
        {
            LocalContext.Value = context;
        }

        /// <summary>
        /// Runs code within a new loader context before firing any pending loaders.
        /// </summary>
        public static Task<T> Run<T>(Func<T> func)
        {
            return Run(_ => func());
        }

        /// <summary>
        /// Runs code within a new loader context before firing any pending loaders.
        /// </summary>
        public static Task Run(Action action)
        {
            return Run(_ => action());
        }

#endif

        /// <summary>
        /// Runs code within a new loader context before firing any pending loaders.
        /// </summary>
        public static async Task<T> Run<T>(Func<DataLoaderContext, T> func)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));

            using (var scope = new DataLoaderScope())
            {
                var result = func(scope.Context);
                await scope.CompleteAsync().ConfigureAwait(false);
                return result;
            }
        }

        /// <summary>
        /// Runs code within a new loader context before firing any pending loaders.
        /// </summary>
        public static async Task Run(Action<DataLoaderContext> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            using (var scope = new DataLoaderScope())
            {
                action(scope.Context);
                await scope.CompleteAsync();
            }
        }
    }
}