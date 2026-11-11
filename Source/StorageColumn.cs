using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace StorageOverview
{
    /// <summary>
    /// One column of the overview: a stockpile zone, a storage group, or a storage
    /// building that is not in a group (members of a group share the group's settings,
    /// so they are shown once). A merged column stands for several storages with
    /// identical settings; reads use the first member and writes go to all of them.
    /// </summary>
    public class StorageColumn
    {
        public readonly IStoreSettingsParent owner;
        public readonly string label;
        public readonly StorageSettings settings;

        /// <summary>Defs the vanilla storage tab of this owner would list.</summary>
        public readonly HashSet<ThingDef> visibleDefs = new HashSet<ThingDef>();

        /// <summary>Special filters (allow rotten, ...) the vanilla storage tab would list.</summary>
        public readonly HashSet<SpecialThingFilterDef> visibleSpecials = new HashSet<SpecialThingFilterDef>();

        public readonly bool hitPointsConfigurable;
        public readonly bool qualityConfigurable;

        /// <summary>The storages this column stands for; just this one unless merged.</summary>
        public readonly List<StorageColumn> members;

        private static List<ThingDef> storableDefs;

        public StorageColumn(IStoreSettingsParent owner, string label)
        {
            this.owner = owner;
            this.label = label;
            members = new List<StorageColumn> { this };
            settings = owner.GetStoreSettings();
            ThingFilter parentFilter = owner.GetParentStoreSettings()?.filter;
            hitPointsConfigurable = parentFilter?.allowedHitPointsConfigurable ?? true;
            qualityConfigurable = parentFilter?.allowedQualitiesConfigurable ?? true;
            foreach (ThingDef def in ThingCategoryNodeDatabase.RootNode.catDef.DescendantThingDefs)
            {
                if (IsVisible(def, parentFilter))
                {
                    visibleDefs.Add(def);
                }
            }
            foreach (SpecialThingFilterDef sf in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading)
            {
                if (IsVisible(sf, parentFilter))
                {
                    visibleSpecials.Add(sf);
                }
            }
        }

        private StorageColumn(List<StorageColumn> group)
        {
            StorageColumn first = group[0];
            owner = first.owner;
            settings = first.settings;
            visibleDefs = first.visibleDefs;
            visibleSpecials = first.visibleSpecials;
            hitPointsConfigurable = first.hitPointsConfigurable;
            qualityConfigurable = first.qualityConfigurable;
            members = group;
            bool sameName = group.All(m => m.label == first.label);
            label = sameName ? first.label + "×" + group.Count : first.label + " +" + (group.Count - 1);
        }

        public ThingFilter Filter => settings.filter;

        /// <summary>Stack counts by def of what this single storage holds right now.</summary>
        public Dictionary<ThingDef, int> CountHeld()
        {
            var counts = new Dictionary<ThingDef, int>();
            IEnumerable<Thing> things = owner switch
            {
                StorageGroup group => group.HeldThings,
                ISlotGroupParent parent => parent.GetSlotGroup()?.HeldThings,
                _ => null,
            };
            if (things == null)
            {
                return counts;
            }
            foreach (Thing thing in things)
            {
                ThingDef def = thing is MinifiedThing minified && minified.InnerThing != null
                    ? minified.InnerThing.def
                    : thing.def;
                counts.TryGetValue(def, out int count);
                counts[def] = count + thing.stackCount;
            }
            return counts;
        }

        public bool IsMerged => members.Count > 1;

        public bool NameMatches(QuickSearchFilter search) => members.Any(m => search.Matches(m.label));

        /// <summary>Member names with counts, for the header tooltip.</summary>
        public string MemberNames()
        {
            return string.Join("\n", members.GroupBy(m => m.label).Select(g => g.Count() > 1 ? g.Key + " ×" + g.Count() : g.Key));
        }

        public StoragePriority Priority
        {
            get => settings.Priority;
            set
            {
                foreach (StorageColumn m in members)
                {
                    m.settings.Priority = value;
                }
            }
        }

        public void SetAllow(ThingDef def, bool allow)
        {
            foreach (StorageColumn m in members)
            {
                m.Filter.SetAllow(def, allow);
            }
        }

        public void SetAllow(SpecialThingFilterDef sf, bool allow)
        {
            foreach (StorageColumn m in members)
            {
                m.Filter.SetAllow(sf, allow);
            }
        }

        public void SetHitPoints(FloatRange range)
        {
            foreach (StorageColumn m in members)
            {
                m.Filter.AllowedHitPointsPercents = range;
            }
        }

        public void SetQuality(QualityRange range)
        {
            foreach (StorageColumn m in members)
            {
                m.Filter.AllowedQualityLevels = range;
            }
        }

        /// <summary>
        /// Everything the overview shows for this storage, as one string: which defs and
        /// special filters it can list and allows, priority, and the two ranges. Equal
        /// signatures mean the columns would look and behave the same.
        /// </summary>
        private string Signature()
        {
            storableDefs ??= ThingCategoryNodeDatabase.RootNode.catDef.DescendantThingDefs.Distinct().ToList();
            var sb = new StringBuilder(storableDefs.Count + 64);
            ThingFilter filter = Filter;
            foreach (ThingDef def in storableDefs)
            {
                sb.Append(!visibleDefs.Contains(def) ? '-' : filter.Allows(def) ? '1' : '0');
            }
            sb.Append('|');
            foreach (SpecialThingFilterDef sf in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading)
            {
                sb.Append(!visibleSpecials.Contains(sf) ? '-' : filter.Allows(sf) ? '1' : '0');
            }
            sb.Append('|').Append((int)settings.Priority);
            if (hitPointsConfigurable)
            {
                sb.Append('|').Append(filter.AllowedHitPointsPercents.min).Append('-').Append(filter.AllowedHitPointsPercents.max);
            }
            if (qualityConfigurable)
            {
                sb.Append('|').Append((int)filter.AllowedQualityLevels.min).Append('-').Append((int)filter.AllowedQualityLevels.max);
            }
            return sb.ToString();
        }

        /// <summary>Folds columns with identical signatures into one, keeping the input order.</summary>
        public static List<StorageColumn> MergeIdentical(List<StorageColumn> columns)
        {
            var groups = new Dictionary<string, List<StorageColumn>>();
            var order = new List<List<StorageColumn>>();
            foreach (StorageColumn column in columns)
            {
                string key = column.Signature();
                if (!groups.TryGetValue(key, out List<StorageColumn> group))
                {
                    group = new List<StorageColumn>();
                    groups[key] = group;
                    order.Add(group);
                }
                group.Add(column);
            }
            return order.Select(g => g.Count == 1 ? g[0] : new StorageColumn(g)).ToList();
        }

        /// <summary>
        /// Same rule as Listing_TreeThingFilter.Visible(SpecialThingFilterDef, node) with the
        /// root node, plus the filters ITab_Storage always hides.
        /// </summary>
        private bool IsVisible(SpecialThingFilterDef sf, ThingFilter parentFilter)
        {
            if (!sf.configurable || StorageTabHidden(sf))
            {
                return false;
            }
            if (parentFilter != null)
            {
                if (!parentFilter.Allows(sf) || parentFilter.hiddenSpecialFilters.Contains(sf))
                {
                    return false;
                }
            }
            foreach (ThingDef def in visibleDefs)
            {
                if (sf.Worker.CanEverMatch(def))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Mirror of ITab_Storage.HiddenSpecialThingFilters.</summary>
        private static bool StorageTabHidden(SpecialThingFilterDef sf)
        {
            return ModsConfig.IdeologyActive
                && (sf == SpecialThingFilterDefOf.AllowVegetarian
                    || sf == SpecialThingFilterDefOf.AllowCarnivore
                    || sf == SpecialThingFilterDefOf.AllowCannibal
                    || sf == SpecialThingFilterDefOf.AllowInsectMeat);
        }

        /// <summary>Same rule as Listing_TreeThingFilter.Visible(ThingDef), without forced hides.</summary>
        private static bool IsVisible(ThingDef def, ThingFilter parentFilter)
        {
            if (!def.PlayerAcquirable || def.virtualDefParent != null)
            {
                return false;
            }
            if (parentFilter != null)
            {
                if (!parentFilter.Allows(def) || parentFilter.IsAlwaysDisallowedDueToSpecialFilters(def))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Collects every storage of the map, one column per settings object.</summary>
        public static List<StorageColumn> CollectFrom(Map map)
        {
            var columns = new List<StorageColumn>();
            var seen = new HashSet<StorageSettings>();
            foreach (SlotGroup group in map.haulDestinationManager.AllGroupsListForReading)
            {
                ISlotGroupParent parent = group.parent;
                IStoreSettingsParent owner = parent;
                string label;
                if (parent is IStorageGroupMember member && member.Group != null)
                {
                    owner = member.Group;
                    label = member.Group.RenamableLabel;
                }
                else if (parent is Zone zone)
                {
                    label = zone.label;
                }
                else
                {
                    label = parent.SlotYielderLabel();
                }
                if (!owner.StorageTabVisible)
                {
                    continue;
                }
                StorageSettings settings = owner.GetStoreSettings();
                if (settings == null || !seen.Add(settings))
                {
                    continue;
                }
                columns.Add(new StorageColumn(owner, label));
            }
            return columns
                .OrderByDescending(c => c.settings.Priority)
                .ThenBy(c => c.label)
                .ToList();
        }
    }
}
