using System;
using System.Linq;
using System.Threading;
using GraphQL.Types;
using Microsoft.EntityFrameworkCore;

namespace DataLoader.StarWars.Schema
{
    public class HumanType : ObjectGraphType<Human>
    {
        public HumanType()
        {
            Name = "Human";
            Field(h => h.Name);
            Field(h => h.HumanId);
            Field(h => h.HomePlanet);
            Interface<CharacterInterface>();

            FieldAsync<ListGraphType<CharacterInterface>>(
                name: "friends",
                resolve: async ctx => await ctx.GetDataLoader(async ids =>
                    {
                        var db = ctx.GetDataContext();
                        return (await db.Friendships
                                .Where(f => ids.Contains(f.HumanId))
                                .Select(f => new { Key = f.HumanId, f.Droid })
                                .ToListAsync())
                            .ToLookup(x => x.Key, x => x.Droid);
                    }).LoadAsync(ctx.Source.HumanId));

            FieldAsync<ListGraphType<EpisodeType>>(
                name: "appearsIn",
                resolve: async ctx => await ctx.GetDataLoader(async ids =>
                {
                    var db = ctx.GetDataContext();
                    return (await db.HumanAppearances
                            .Where(ha => ids.Contains(ha.HumanId))
                            .Select(ha => new { Key = ha.HumanId, ha.Episode })
                            .ToListAsync())
                        .ToLookup(x => x.Key, x => x.Episode);
                }).LoadAsync(ctx.Source.HumanId));
        }
    }
}
