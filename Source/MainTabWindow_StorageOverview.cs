using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace StorageOverview
{
    /// <summary>
    /// Main tab with the vanilla item tree on the left and one checkbox column per
    /// storage. Rows and columns are virtualised: only the cells on screen are drawn.
    /// </summary>
    public class MainTabWindow_StorageOverview : MainTabWindow
    {
        private const float RowHeight = 24f;
        private const float ColumnWidth = 26f;
        private const float TreeWidth = 300f;
        private const float HeaderHeight = 180f;
        private const float RangeRowHeight = 96f;
        private const float HitPointsRowY = RowHeight;
        private const float QualityRowY = RowHeight + RangeRowHeight;
        /// <summary>Priority, hit points and quality rows above the item rows.</summary>
        private const float RangeBlockHeight = RowHeight + RangeRowHeight * 2f;
        private const int QualitySteps = 6;
        private const float IndentWidth = 12f;
        private const float MarkBoxWidth = 24f;
        private const float CheckSize = 20f;
        private const float ScrollBarSize = 16f;
        private const int StateCacheFrames = 30;
        /// <summary>Stored contents are recounted about once a second.</summary>
        private const int HeldRefreshFrames = 60;

        /// <summary>Soft tint on cells whose storage currently holds the item.</summary>
        private static readonly Color HeldColor = new Color(0.4f, 0.65f, 1f, 0.32f);

        private static readonly Color NoMatchColor = Color.grey;

        private enum RowKind { Category, Def, Special, Undiscovered }

        /// <summary>Which item rows to list.</summary>
        private enum ViewMode { All, OnMap, Unstored }

        private class Row
        {
            public RowKind kind;
            public int indent;
            public TreeNode_ThingCategory node;
            public ThingDef def;
            public SpecialThingFilterDef special;
            public List<ThingDef> undiscovered;
            public bool dimmed;
        }

        private readonly List<Row> rows = new List<Row>();
        private readonly HashSet<ThingCategoryDef> expanded = new HashSet<ThingCategoryDef>();
        private readonly HashSet<ThingDef> anyVisible = new HashSet<ThingDef>();
        private readonly HashSet<SpecialThingFilterDef> anyVisibleSpecial = new HashSet<SpecialThingFilterDef>();
        private readonly Dictionary<ThingCategoryDef, bool> categoryShown = new Dictionary<ThingCategoryDef, bool>();
        private readonly Dictionary<ThingCategoryDef, MultiCheckboxState?[]> categoryStates =
            new Dictionary<ThingCategoryDef, MultiCheckboxState?[]>();
        private readonly QuickSearchWidget search = new QuickSearchWidget();
        private readonly QuickSearchWidget columnSearch = new QuickSearchWidget();

        private ViewMode viewMode = ViewMode.All;

        /// <summary>Items marked in the tree; columns accepting more of them come first.</summary>
        private readonly HashSet<ThingDef> marked = new HashSet<ThingDef>();
        /// <summary>Marked items each drawn column accepts, taken when the columns were ordered.
        /// Not updated on every tick so columns do not move while being edited.</summary>
        private readonly Dictionary<StorageColumn, int> markScores = new Dictionary<StorageColumn, int>();
        /// <summary>Columns with a score above zero; a divider is drawn after them.</summary>
        private int relatedColumnCount;

        /// <summary>What each single storage (unmerged) holds, by def.</summary>
        private readonly Dictionary<StorageColumn, Dictionary<ThingDef, int>> held =
            new Dictionary<StorageColumn, Dictionary<ThingDef, int>>();
        private readonly Dictionary<ThingCategoryDef, bool[]> categoryHeld = new Dictionary<ThingCategoryDef, bool[]>();
        private int heldFrame = -1;

        /// <summary>Things of each def on the map (minified buildings by their inner def).</summary>
        private readonly Dictionary<ThingDef, int> mapCounts = new Dictionary<ThingDef, int>();
        /// <summary>Defs no storage allows; fixed until the rows are rebuilt so ticking a box
        /// does not make its row vanish under the mouse.</summary>
        private readonly HashSet<ThingDef> unstored = new HashSet<ThingDef>();
        /// <summary>Highest priority among storages allowing a def (null: none).</summary>
        private readonly Dictionary<ThingDef, StoragePriority?> destinationPriority =
            new Dictionary<ThingDef, StoragePriority?>();

        /// <summary>Every storage of the map.</summary>
        private List<StorageColumn> allColumns = new List<StorageColumn>();
        /// <summary>Storages whose name matches the storage search; these are drawn.</summary>
        private List<StorageColumn> columns = new List<StorageColumn>();
        private Map map;
        // Storage count at the last rebuild; a change means a storage was built or removed.
        private int lastStorageCount = -1;
        private int stateCacheFrame = -1;
        private Vector2 scroll;

        // Range bar being dragged (hit points or quality of one column).
        private StorageColumn dragColumn;
        private bool dragQuality;
        private bool dragMax;

        private const float MainButtonBarHeight = 35f;
        /// <summary>
        /// Fixed tab width. The strip on the right stays map, so a storage picked from the
        /// header can be shown there without the table resizing under the mouse.
        /// </summary>
        private const float WidthFraction = 0.8f;

        // Row/column lists must not change while a frame is being drawn (the draw loops
        // index into them), so UI actions only mark them and they are rebuilt next frame.
        private bool rowsDirty;

        // Show storages with identical settings as one column (on by default).
        private bool mergeIdentical = true;

        // Merge by item filters only, for adjusting the items of similar storages together.
        // Members of one column may then differ in priority, hit points and quality, so
        // those rows and copy/paste (which carries them) are disabled while it is on.
        private bool mergeItemsOnly;

        private bool ItemsOnly => mergeIdentical && mergeItemsOnly;

        // Rename dialog opened from a header; names are re-read once it closes.
        private Window renameDialog;
        private bool columnsDirty;

        public override Vector2 RequestedTabSize => new Vector2(Mathf.Round(UI.screenWidth * WidthFraction), UI.screenHeight - MainButtonBarHeight);

        private QuickSearchFilter Search => search.filter;

        /// <summary>Rows are narrowed down and categories open on their own.</summary>
        private bool Filtering => Search.Active || viewMode != ViewMode.All;

        /// <summary>Rows no shown storage accepts (all crosses); drawn dimmed.</summary>
        private readonly Dictionary<Row, bool> rowRejected = new Dictionary<Row, bool>();

        private void InvalidateStates()
        {
            categoryStates.Clear();
            rowRejected.Clear();
            categoryHeld.Clear();
            destinationPriority.Clear();
        }

        public override void PreOpen()
        {
            base.PreOpen();
            // Always start at the leftmost column: marks sort the related columns there, and
            // nothing in the table shows which column is the selected storage.
            scroll.x = 0f;
            Rebuild();
        }

        private void Rebuild()
        {
            map = Find.CurrentMap;
            allColumns = map != null ? StorageColumn.CollectFrom(map) : new List<StorageColumn>();
            CountMapThings();
            ApplyColumnFilter();
            CountHeld(logTime: true);
            lastStorageCount = StorageCount(map);
        }

        private void CountMapThings()
        {
            mapCounts.Clear();
            if (map == null)
            {
                return;
            }
            foreach (Thing thing in map.listerThings.AllThings)
            {
                ThingDef def = thing is MinifiedThing minified && minified.InnerThing != null
                    ? minified.InnerThing.def
                    : thing.def;
                mapCounts.TryGetValue(def, out int count);
                mapCounts[def] = count + thing.stackCount;
            }
        }

        /// <summary>Recounts what every storage holds; one pass over the stored stacks.</summary>
        private void CountHeld(bool logTime)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            held.Clear();
            categoryHeld.Clear();
            int entries = 0;
            foreach (StorageColumn column in allColumns)
            {
                Dictionary<ThingDef, int> counts = column.CountHeld();
                held[column] = counts;
                entries += counts.Count;
            }
            heldFrame = Time.frameCount;
            if (logTime && Prefs.DevMode)
            {
                Log.Message($"[StorageOverview] counted stored items of {allColumns.Count} storages ({entries} def entries) in {watch.Elapsed.TotalMilliseconds:F2} ms");
            }
        }

        private int HeldCount(StorageColumn column, ThingDef def)
        {
            int total = 0;
            foreach (StorageColumn member in column.members)
            {
                if (held.TryGetValue(member, out Dictionary<ThingDef, int> counts) && counts.TryGetValue(def, out int count))
                {
                    total += count;
                }
            }
            return total;
        }

        private bool CategoryHeld(ThingCategoryDef cat, int columnIndex)
        {
            if (!categoryHeld.TryGetValue(cat, out bool[] states))
            {
                states = new bool[columns.Count];
                for (int i = 0; i < columns.Count; i++)
                {
                    foreach (StorageColumn member in columns[i].members)
                    {
                        if (held.TryGetValue(member, out Dictionary<ThingDef, int> counts)
                            && System.Linq.Enumerable.Any(counts.Keys, def => def.IsWithinCategory(cat)))
                        {
                            states[i] = true;
                            break;
                        }
                    }
                }
                categoryHeld[cat] = states;
            }
            return states[columnIndex];
        }

        private static void DrawHeldTint(Rect rect, bool isHeld)
        {
            if (isHeld)
            {
                Widgets.DrawBoxSolid(rect.ContractedBy(1f), HeldColor);
            }
        }

        private static int StorageCount(Map m) => m?.haulDestinationManager.AllGroupsListForReading.Count ?? 0;

        private void ApplyColumnFilter()
        {
            QuickSearchFilter filter = columnSearch.filter;
            columns = filter.Active ? allColumns.FindAll(c => c.NameMatches(filter)) : new List<StorageColumn>(allColumns);
            if (mergeIdentical)
            {
                columns = StorageColumn.MergeIdentical(columns, itemsOnly: mergeItemsOnly);
            }
            OrderByMarks();
            anyVisible.Clear();
            anyVisibleSpecial.Clear();
            foreach (StorageColumn column in columns)
            {
                anyVisible.UnionWith(column.visibleDefs);
                anyVisibleSpecial.UnionWith(column.visibleSpecials);
            }
            categoryShown.Clear();
            InvalidateStates();
            BuildRows();
        }

        // ---------------------------------------------------------------- rows

        private void BuildRows()
        {
            rows.Clear();
            unstored.Clear();
            if (viewMode == ViewMode.Unstored)
            {
                foreach (ThingDef def in anyVisible)
                {
                    if (!AllowedAnywhere(def))
                    {
                        unstored.Add(def);
                    }
                }
            }
            AddChildren(ThingCategoryNodeDatabase.RootNode, 0, subtreeMatched: false);
        }

        /// <summary>Whether any storage of the map (search and merging aside) allows the def.</summary>
        private bool AllowedAnywhere(ThingDef def)
        {
            foreach (StorageColumn column in allColumns)
            {
                if (column.visibleDefs.Contains(def) && column.Filter.Allows(def))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Stable sort: related columns first, then priority, then marked items accepted
        /// (partial categories count partly).
        /// </summary>
        private void OrderByMarks()
        {
            markScores.Clear();
            relatedColumnCount = 0;
            if (marked.Count == 0)
            {
                return;
            }
            foreach (StorageColumn column in columns)
            {
                int score = 0;
                foreach (ThingDef def in marked)
                {
                    if (column.visibleDefs.Contains(def) && column.Filter.Allows(def))
                    {
                        score++;
                    }
                }
                markScores[column] = score;
                if (score > 0)
                {
                    relatedColumnCount++;
                }
            }
            // Related columns first, then by priority (where haulers actually take the items),
            // then by score; ties keep the original order (stable sort).
            columns = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.ThenByDescending(
                    System.Linq.Enumerable.ThenByDescending(
                        System.Linq.Enumerable.OrderByDescending(columns, c => markScores[c] > 0),
                        TopPriority),
                    c => markScores[c]));
        }

        /// <summary>
        /// Highest priority among the column's members; members differ only when merging by
        /// item filters, and the highest one is where haulers take the items first.
        /// </summary>
        private static StoragePriority TopPriority(StorageColumn column)
        {
            StoragePriority top = column.settings.Priority;
            foreach (StorageColumn m in column.members)
            {
                if (m.settings.Priority > top)
                {
                    top = m.settings.Priority;
                }
            }
            return top;
        }

        private bool Unrelated(StorageColumn column) => marked.Count > 0 && markScores.TryGetValue(column, out int s) && s == 0;

        private void SetMarked(IEnumerable<ThingDef> defs, bool on)
        {
            foreach (ThingDef def in defs)
            {
                if (!anyVisible.Contains(def))
                {
                    continue;
                }
                if (on)
                {
                    marked.Add(def);
                }
                else
                {
                    marked.Remove(def);
                }
            }
            columnsDirty = true;
            scroll.x = 0f;
        }

        private MultiCheckboxState MarkState(IEnumerable<ThingDef> defs)
        {
            int visible = 0;
            int on = 0;
            foreach (ThingDef def in defs)
            {
                if (anyVisible.Contains(def))
                {
                    visible++;
                    if (marked.Contains(def))
                    {
                        on++;
                    }
                }
            }
            return on == 0 ? MultiCheckboxState.Off : on == visible ? MultiCheckboxState.On : MultiCheckboxState.Partial;
        }

        private bool InScope(ThingDef def)
        {
            switch (viewMode)
            {
                case ViewMode.OnMap:
                    return mapCounts.ContainsKey(def);
                case ViewMode.Unstored:
                    return unstored.Contains(def);
                default:
                    return true;
            }
        }

        /// <summary>Whether a def gets a row (or a place in the undiscovered row).</summary>
        private bool DefListed(ThingDef def, bool subtreeMatched)
        {
            if (!anyVisible.Contains(def) || !InScope(def))
            {
                return false;
            }
            if (Find.HiddenItemsManager.Hidden(def))
            {
                // Vanilla never lists undiscovered items while searching.
                return !Search.Active;
            }
            return !Search.Active || subtreeMatched || Search.Matches(def);
        }

        /// <summary>
        /// Mirrors Listing_TreeThingFilter.DoCategoryChildren for the union of all columns,
        /// narrowed by the item search and the view mode.
        /// </summary>
        private void AddChildren(TreeNode_ThingCategory node, int indent, bool subtreeMatched)
        {
            bool searching = Search.Active;
            if (viewMode == ViewMode.All)
            {
                foreach (SpecialThingFilterDef sf in node.catDef.childSpecialFilters)
                {
                    if (anyVisibleSpecial.Contains(sf))
                    {
                        rows.Add(new Row
                        {
                            kind = RowKind.Special, indent = indent, special = sf,
                            dimmed = searching && !subtreeMatched && !Search.Matches(sf),
                        });
                    }
                }
            }
            foreach (TreeNode_ThingCategory child in node.ChildCategoryNodes)
            {
                if (!IsCategoryShown(child.catDef))
                {
                    continue;
                }
                bool matched = subtreeMatched || (searching && Search.Matches(child.catDef.label));
                if (Filtering && !SubtreeListsAnything(child, matched))
                {
                    continue;
                }
                rows.Add(new Row
                {
                    kind = RowKind.Category, indent = indent, node = child,
                    dimmed = searching && !matched,
                });
                if (Filtering || expanded.Contains(child.catDef))
                {
                    AddChildren(child, indent + 1, matched);
                }
            }
            List<ThingDef> undiscovered = null;
            foreach (ThingDef def in node.catDef.SortedChildThingDefs)
            {
                if (!DefListed(def, subtreeMatched))
                {
                    continue;
                }
                if (Find.HiddenItemsManager.Hidden(def))
                {
                    (undiscovered ??= new List<ThingDef>()).Add(def);
                    continue;
                }
                rows.Add(new Row { kind = RowKind.Def, indent = indent, def = def });
            }
            if (undiscovered != null)
            {
                rows.Add(new Row { kind = RowKind.Undiscovered, indent = indent, undiscovered = undiscovered });
            }
        }

        private bool SubtreeListsAnything(TreeNode_ThingCategory node, bool subtreeMatched)
        {
            foreach (ThingDef def in node.catDef.DescendantThingDefs)
            {
                if (DefListed(def, subtreeMatched))
                {
                    return true;
                }
            }
            if (viewMode == ViewMode.All && Search.Active)
            {
                foreach (SpecialThingFilterDef sf in node.catDef.DescendantSpecialThingFilterDefs)
                {
                    if (anyVisibleSpecial.Contains(sf) && (subtreeMatched || Search.Matches(sf)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool IsCategoryShown(ThingCategoryDef cat)
        {
            if (!categoryShown.TryGetValue(cat, out bool shown))
            {
                shown = false;
                foreach (ThingDef def in cat.DescendantThingDefs)
                {
                    if (anyVisible.Contains(def))
                    {
                        shown = true;
                        break;
                    }
                }
                categoryShown[cat] = shown;
            }
            return shown;
        }

        // -------------------------------------------------------------- layout

        public override void DoWindowContents(Rect inRect)
        {
            // No periodic rebuild: while this full-screen tab is open other storage UI is
            // covered, so settings only change through this table. Merged columns are
            // therefore regrouped only on open, map change, toggle or storage search.
            if (Find.CurrentMap != map || StorageCount(map) != lastStorageCount)
            {
                Rebuild();
            }
            if (renameDialog != null && !renameDialog.IsOpen)
            {
                renameDialog = null;
                Rebuild();
            }
            if (columnsDirty)
            {
                columnsDirty = false;
                rowsDirty = false;
                ApplyColumnFilter();
            }
            if (rowsDirty)
            {
                rowsDirty = false;
                BuildRows();
            }
            if (Time.frameCount - heldFrame > HeldRefreshFrames)
            {
                CountHeld(logTime: false);
            }
            if (Time.frameCount - stateCacheFrame > StateCacheFrames)
            {
                InvalidateStates();
                stateCacheFrame = Time.frameCount;
            }
            Text.Font = GameFont.Small;
            if (allColumns.Count == 0)
            {
                Widgets.Label(inRect, "StorageOverview_NoStorage".Translate());
                return;
            }

            float columnsX = inRect.x + TreeWidth;
            float bodyY = inRect.y + HeaderHeight;
            Rect cornerRect = new Rect(inRect.x, inRect.y, TreeWidth, HeaderHeight);
            Rect headerRect = new Rect(columnsX, inRect.y, inRect.width - TreeWidth - ScrollBarSize, HeaderHeight);
            Rect treeRect = new Rect(inRect.x, bodyY, TreeWidth, inRect.yMax - bodyY - ScrollBarSize);
            Rect bodyRect = new Rect(columnsX, bodyY, inRect.width - TreeWidth, inRect.yMax - bodyY);
            Rect viewRect = new Rect(0f, 0f, columns.Count * ColumnWidth + ScrollBarSize,
                RangeBlockHeight + rows.Count * RowHeight + ScrollBarSize);

            HandleWheel(treeRect, headerRect, bodyRect, viewRect);

            // Hover row/column from the mouse position, shared by all panes.
            Vector2 mouse = Event.current.mousePosition;
            int hoverRow = -1;
            int hoverColumn = -1;
            if (mouse.y >= bodyRect.y && mouse.y < bodyRect.yMax && mouse.x < bodyRect.xMax)
            {
                float y = mouse.y - bodyRect.y + scroll.y - RangeBlockHeight;
                hoverRow = y >= 0f ? Mathf.FloorToInt(y / RowHeight) : -1;
            }
            if (mouse.x >= bodyRect.x && mouse.x < bodyRect.xMax && mouse.y < bodyRect.yMax)
            {
                hoverColumn = Mathf.FloorToInt((mouse.x - bodyRect.x + scroll.x) / ColumnWidth);
            }

            bool rangesVisible = scroll.y < RangeBlockHeight;
            int firstRow = Mathf.Max(0, Mathf.FloorToInt((scroll.y - RangeBlockHeight) / RowHeight));
            int lastRow = Mathf.Min(rows.Count - 1, Mathf.CeilToInt((scroll.y + bodyRect.height - RangeBlockHeight) / RowHeight));
            int firstColumn = Mathf.Max(0, Mathf.FloorToInt(scroll.x / ColumnWidth));
            int lastColumn = Mathf.Min(columns.Count - 1, Mathf.CeilToInt((scroll.x + bodyRect.width) / ColumnWidth));

            float visibleRight = scroll.x + bodyRect.width - ScrollBarSize;
            float visibleBottom = scroll.y + bodyRect.height - ScrollBarSize;
            while (lastColumn >= firstColumn && (lastColumn + 1) * ColumnWidth > visibleRight)
            {
                lastColumn--;
            }
            while (lastRow >= firstRow && RangeBlockHeight + (lastRow + 1) * RowHeight > visibleBottom)
            {
                lastRow--;
            }

            CellRect mapView = VisibleMapCells();
            DrawCorner(cornerRect);

            Widgets.BeginScrollView(bodyRect, ref scroll, viewRect);
            for (int c = firstColumn; c <= lastColumn; c++)
            {
                Rect columnRect = new Rect(c * ColumnWidth, scroll.y, ColumnWidth, bodyRect.height - ScrollBarSize);
                if (c == hoverColumn)
                {
                    Widgets.DrawHighlight(columnRect);
                }
                else if (c % 2 == 1)
                {
                    Widgets.DrawLightHighlight(columnRect);
                }
            }
            if (rangesVisible)
            {
                for (int c = firstColumn; c <= lastColumn; c++)
                {
                    if (RowHeight <= visibleBottom)
                    {
                        if (!ItemsOnly)
                        {
                            DrawPriorityCell(new Rect(c * ColumnWidth, 0f, ColumnWidth, RowHeight), columns[c]);
                        }
                    }
                    if (!ItemsOnly && HitPointsRowY + RangeRowHeight <= visibleBottom)
                    {
                        DrawRangeCell(new Rect(c * ColumnWidth, HitPointsRowY, ColumnWidth, RangeRowHeight), columns[c], quality: false);
                    }
                    if (!ItemsOnly && QualityRowY + RangeRowHeight <= visibleBottom)
                    {
                        DrawRangeCell(new Rect(c * ColumnWidth, QualityRowY, ColumnWidth, RangeRowHeight), columns[c], quality: true);
                    }
                }
            }
            for (int r = firstRow; r <= lastRow; r++)
            {
                float y = RangeBlockHeight + r * RowHeight;
                if (r == hoverRow)
                {
                    Widgets.DrawHighlight(new Rect(scroll.x, y, bodyRect.width - ScrollBarSize, RowHeight));
                }
                for (int c = firstColumn; c <= lastColumn; c++)
                {
                    DrawCell(new Rect(c * ColumnWidth, y, ColumnWidth, RowHeight), rows[r], c);
                }
                if (RowRejected(rows[r]))
                {
                    Widgets.DrawBoxSolid(new Rect(scroll.x, y, bodyRect.width - ScrollBarSize, RowHeight), DimColor);
                }
                if (rows[r].kind == RowKind.Category)
                {
                    Widgets.DrawBoxSolid(new Rect(scroll.x, y + RowHeight - 1f, bodyRect.width - ScrollBarSize, 1f), CategoryLineColor);
                }
            }
            if (marked.Count > 0)
            {
                for (int c = firstColumn; c <= lastColumn; c++)
                {
                    if (Unrelated(columns[c]))
                    {
                        Widgets.DrawBoxSolid(new Rect(c * ColumnWidth, scroll.y, ColumnWidth, bodyRect.height - ScrollBarSize), new Color(0f, 0f, 0f, 0.45f));
                    }
                }
                DrawMarkDivider(scroll.y, bodyRect.height - ScrollBarSize);
            }
            Widgets.EndScrollView();

            Widgets.BeginGroup(treeRect);
            if (rangesVisible)
            {
                DrawRangeLabels(new Rect(0f, -scroll.y, TreeWidth, RangeBlockHeight));
            }
            for (int r = firstRow; r <= lastRow; r++)
            {
                Rect rowRect = new Rect(0f, RangeBlockHeight + r * RowHeight - scroll.y, TreeWidth, RowHeight);
                if (r == hoverRow)
                {
                    Widgets.DrawHighlight(rowRect);
                }
                DrawRowLabel(rowRect, rows[r]);
                if (RowRejected(rows[r]))
                {
                    Widgets.DrawBoxSolid(rowRect, DimColor);
                }
                if (rows[r].kind == RowKind.Category)
                {
                    // Underline starts at the row's indent so nested groups read as a tree.
                    float x = MarkBoxWidth + rows[r].indent * IndentWidth;
                    Widgets.DrawBoxSolid(new Rect(x, rowRect.yMax - 1f, TreeWidth - x, 1f), CategoryLineColor);
                }
            }
            Widgets.EndGroup();

            Widgets.BeginGroup(headerRect);
            for (int c = firstColumn; c <= lastColumn; c++)
            {
                Rect rect = new Rect(c * ColumnWidth - scroll.x, 0f, ColumnWidth, HeaderHeight);
                if (c == hoverColumn)
                {
                    Widgets.DrawHighlight(rect);
                }
                if (columns[c].AnyMemberIn(mapView))
                {
                    // Storage visible in the map strip right of the table.
                    Widgets.DrawBoxSolid(rect, InViewColor);
                }
                DrawHeader(rect, columns[c]);
                if (Unrelated(columns[c]))
                {
                    Widgets.DrawBoxSolid(rect, new Color(0f, 0f, 0f, 0.45f));
                }
            }
            if (marked.Count > 0)
            {
                DrawMarkDivider(0f, HeaderHeight, -scroll.x);
            }
            Widgets.EndGroup();

            // Rotated names are drawn outside the header group: 1.7 clips by the unrotated
            // rect, which lies left of the column and would be cut by the group.
            for (int c = firstColumn; c <= lastColumn; c++)
            {
                string label = columns[c].label;
                float centerX = headerRect.x + c * ColumnWidth - scroll.x + ColumnWidth / 2f;
                if (UsesRotatedLabel(label) && centerX > headerRect.x && centerX < headerRect.xMax)
                {
                    DrawRotatedLabel(new Vector2(centerX, headerRect.yMax - 4f), label, HeaderHeight - 8f,
                        Unrelated(columns[c]));
                }
            }

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        /// <summary>Vertical line between columns accepting marked items and the rest.</summary>
        private void DrawMarkDivider(float y, float height, float xOffset = 0f)
        {
            float x = relatedColumnCount * ColumnWidth + xOffset;
            Widgets.DrawBoxSolid(new Rect(x - 1f, y, 2f, height), new Color(0.45f, 0.85f, 1f, 0.8f));
        }

        /// <summary>Wheel over the tree scrolls rows; over the column names or with Shift, columns.</summary>
        private void HandleWheel(Rect treeRect, Rect headerRect, Rect bodyRect, Rect viewRect)
        {
            Event e = Event.current;
            if (e.type != EventType.ScrollWheel)
            {
                return;
            }
            float maxX = Mathf.Max(0f, viewRect.width - (bodyRect.width - ScrollBarSize));
            float maxY = Mathf.Max(0f, viewRect.height - (bodyRect.height - ScrollBarSize));
            if (headerRect.Contains(e.mousePosition) || (bodyRect.Contains(e.mousePosition) && e.shift))
            {
                scroll.x = Mathf.Clamp(scroll.x + e.delta.y * ColumnWidth, 0f, maxX);
                e.Use();
            }
            else if (treeRect.Contains(e.mousePosition))
            {
                scroll.y = Mathf.Clamp(scroll.y + e.delta.y * RowHeight, 0f, maxY);
                e.Use();
            }
        }

        private void DrawCorner(Rect rect)
        {
            Text.Font = GameFont.Small;
            float captionWidth = Mathf.Max(
                Text.CalcSize("StorageOverview_SearchItems".Translate()).x,
                Text.CalcSize("StorageOverview_SearchStorages".Translate()).x) + 8f;
            const float gap = 4f;
            float searchHeight = QuickSearchWidget.WidgetHeight;
            Rect searchRect = new Rect(rect.x + captionWidth, rect.yMax - searchHeight - gap, rect.width - captionWidth - 8f, searchHeight);
            Rect columnSearchRect = new Rect(searchRect.x, searchRect.y - searchHeight - gap, searchRect.width, searchHeight);
            float buttonWidth = (rect.width - 8f - 2f * gap) / 3f;
            Rect buttonRect = new Rect(rect.x, columnSearchRect.y - 28f - gap, buttonWidth, 28f);
            Rect itemsOnlyRect = new Rect(rect.x + 16f, buttonRect.y - 24f - gap, rect.width - 24f, 24f);
            Rect mergeRect = new Rect(rect.x, itemsOnlyRect.y - 24f, rect.width - 8f, 24f);
            Rect modeRect = new Rect(rect.x, mergeRect.y - 28f - gap, rect.width - 40f, 28f);
            Rect helpRect = new Rect(modeRect.xMax + 8f, modeRect.y, 24f, 28f);

            if (Widgets.ButtonText(modeRect, ModeLabel(viewMode)))
            {
                var options = new List<FloatMenuOption>();
                foreach (ViewMode mode in new[] { ViewMode.All, ViewMode.OnMap, ViewMode.Unstored })
                {
                    ViewMode local = mode;
                    options.Add(new FloatMenuOption(ModeLabel(local), () =>
                    {
                        viewMode = local;
                        if (local == ViewMode.OnMap)
                        {
                            CountMapThings();
                        }
                        rowsDirty = true;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = Color.gray;
            Widgets.Label(helpRect, "?");
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.DrawHighlightIfMouseover(helpRect);
            TooltipHandler.TipRegion(helpRect, "StorageOverview_Hint".Translate());

            bool merge = mergeIdentical;
            Widgets.CheckboxLabeled(mergeRect, "StorageOverview_MergeIdentical".Translate(), ref merge);
            if (merge != mergeIdentical)
            {
                mergeIdentical = merge;
                columnsDirty = true;
            }
            TooltipHandler.TipRegion(itemsOnlyRect, "StorageOverview_MergeItemsOnlyTip".Translate());
            bool ignore = mergeItemsOnly;
            Widgets.CheckboxLabeled(itemsOnlyRect, "StorageOverview_MergeItemsOnly".Translate(), ref ignore,
                disabled: !mergeIdentical);
            if (ignore != mergeItemsOnly)
            {
                mergeItemsOnly = ignore;
                columnsDirty = true;
            }
            if (Widgets.ButtonText(buttonRect, "StorageOverview_CollapseAll".Translate()))
            {
                expanded.Clear();
                rowsDirty = true;
            }
            if (Widgets.ButtonText(new Rect(buttonRect.xMax + gap, buttonRect.y, buttonRect.width, buttonRect.height),
                "StorageOverview_ExpandAll".Translate()))
            {
                expanded.UnionWith(DefDatabase<ThingCategoryDef>.AllDefsListForReading);
                rowsDirty = true;
            }
            if (Widgets.ButtonText(new Rect(buttonRect.xMax + buttonWidth + 2f * gap, buttonRect.y, buttonRect.width, buttonRect.height),
                "StorageOverview_ClearMarks".Translate(), active: marked.Count > 0))
            {
                marked.Clear();
                columnsDirty = true;
            }
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(rect.x, searchRect.y, captionWidth, searchHeight), "StorageOverview_SearchItems".Translate());
            Widgets.Label(new Rect(rect.x, columnSearchRect.y, captionWidth, searchHeight), "StorageOverview_SearchStorages".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            columnSearch.OnGUI(columnSearchRect, () => columnsDirty = true);
            search.OnGUI(searchRect, () => rowsDirty = true);
        }

        private static readonly Color DimColor = new Color(0f, 0f, 0f, 0.45f);

        /// <summary>Underline below every category row (collapsed or not), marking section breaks.</summary>
        private static readonly Color CategoryLineColor = new Color(1f, 1f, 1f, 0.22f);

        /// <summary>
        /// True when every shown storage that could hold the row's items refuses all of them.
        /// Special filter rows are never dimmed.
        /// </summary>
        private bool RowRejected(Row row)
        {
            if (rowRejected.TryGetValue(row, out bool rejected))
            {
                return rejected;
            }
            rejected = false;
            switch (row.kind)
            {
                case RowKind.Def:
                    rejected = !columns.Exists(c => c.visibleDefs.Contains(row.def) && c.Filter.Allows(row.def));
                    break;
                case RowKind.Undiscovered:
                    rejected = !columns.Exists(c => row.undiscovered.Exists(d => c.visibleDefs.Contains(d) && c.Filter.Allows(d)));
                    break;
                case RowKind.Category:
                    rejected = true;
                    for (int i = 0; i < columns.Count; i++)
                    {
                        MultiCheckboxState? state = CategoryState(row.node.catDef, i);
                        if (state != null && state != MultiCheckboxState.Off)
                        {
                            rejected = false;
                            break;
                        }
                    }
                    break;
            }
            rowRejected[row] = rejected;
            return rejected;
        }

        private static readonly Color InViewColor = new Color(1f, 1f, 1f, 0.16f);

        /// <summary>Map cells shown in the strip right of this tab (screen space is bottom-up).</summary>
        private CellRect VisibleMapCells()
        {
            if (Find.CurrentMap == null || Find.Camera == null)
            {
                return new CellRect(-1, -1, 0, 0);
            }
            IntVec3 a = UI.UIToMapPosition(new Vector2(windowRect.xMax, MainButtonBarHeight)).ToIntVec3();
            IntVec3 b = UI.UIToMapPosition(new Vector2(UI.screenWidth, UI.screenHeight)).ToIntVec3();
            return CellRect.FromLimits(Mathf.Min(a.x, b.x), Mathf.Min(a.z, b.z), Mathf.Max(a.x, b.x), Mathf.Max(a.z, b.z));
        }

        private static string ModeLabel(ViewMode mode)
        {
            switch (mode)
            {
                case ViewMode.OnMap:
                    return "StorageOverview_ViewOnMap".Translate();
                case ViewMode.Unstored:
                    return "StorageOverview_ViewUnstored".Translate();
                default:
                    return "StorageOverview_ViewAll".Translate();
            }
        }

        private void DrawRangeLabels(Rect rect)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = ItemsOnly ? Color.gray : Color.white;
            Widgets.Label(new Rect(rect.x + 22f, rect.y, rect.width - 22f, RowHeight), "Priority".Translate().CapitalizeFirst());
            Widgets.Label(new Rect(rect.x + 22f, rect.y + HitPointsRowY, rect.width - 22f, RangeRowHeight), "HitPointsBasic".Translate().CapitalizeFirst());
            Widgets.Label(new Rect(rect.x + 22f, rect.y + QualityRowY, rect.width - 22f, RangeRowHeight), "Quality".Translate().CapitalizeFirst());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.DrawLineHorizontal(rect.x, rect.y + HitPointsRowY, rect.width);
            Widgets.DrawLineHorizontal(rect.x, rect.y + QualityRowY, rect.width);
            Widgets.DrawLineHorizontal(rect.x, rect.yMax - 1f, rect.width);
        }

        // ----------------------------------------------------------- tree pane

        private void DrawRowLabel(Rect rect, Row row)
        {
            DrawMarkBox(new Rect(rect.x + 2f, rect.y + (RowHeight - 18f) / 2f, 18f, 18f), row);
            float x = rect.x + MarkBoxWidth + row.indent * IndentWidth;
            Rect iconRect = new Rect(x, rect.y + (RowHeight - 18f) / 2f, 18f, 18f);
            Rect labelRect = new Rect(x + 22f, rect.y, rect.xMax - x - 22f, RowHeight);
            Text.Anchor = TextAnchor.MiddleLeft;
            if (row.dimmed)
            {
                GUI.color = NoMatchColor;
            }
            switch (row.kind)
            {
                case RowKind.Category:
                {
                    bool open = Filtering || expanded.Contains(row.node.catDef);
                    GUI.DrawTexture(iconRect, open ? TexButton.Collapse : TexButton.Reveal);
                    Widgets.Label(labelRect, row.node.LabelCap.Truncate(labelRect.width));
                    if (!Filtering && Widgets.ButtonInvisible(new Rect(x, rect.y, rect.xMax - x, RowHeight)))
                    {
                        if (open)
                        {
                            expanded.Remove(row.node.catDef);
                        }
                        else
                        {
                            expanded.Add(row.node.catDef);
                        }
                        rowsDirty = true;
                    }
                    break;
                }
                case RowKind.Def:
                {
                    ThingDef def = row.def;
                    Widgets.DefIcon(iconRect, def, null, 1f, null, drawPlaceholder: true);
                    if (mapCounts.TryGetValue(def, out int count))
                    {
                        // Map count on the right, like the vanilla storage tab.
                        string countText = count.ToStringCached();
                        float countWidth = Text.CalcSize(countText).x + 6f;
                        Text.Anchor = TextAnchor.MiddleRight;
                        GUI.color = new Color(0.75f, 0.75f, 0.35f);
                        Widgets.Label(new Rect(labelRect.xMax - countWidth - 4f, rect.y, countWidth, RowHeight), countText);
                        GUI.color = Color.white;
                        Text.Anchor = TextAnchor.MiddleLeft;
                        labelRect.width -= countWidth + 4f;
                    }
                    Widgets.Label(labelRect, def.LabelCap.Truncate(labelRect.width));
                    TooltipHandler.TipRegion(rect, new TipSignal(() => def.DescriptionDetailed, def.shortHash));
                    break;
                }
                case RowKind.Special:
                    Widgets.Label(labelRect, ("*" + row.special.LabelCap).Truncate(labelRect.width));
                    TooltipHandler.TipRegion(rect, row.special.description);
                    break;
                case RowKind.Undiscovered:
                    GUI.color = Color.gray;
                    Widgets.Label(labelRect, "UndiscoveredItemLabel".Translate());
                    TooltipHandler.TipRegion(rect, "UndiscoveredItemDesc".Translate());
                    break;
            }
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private void DrawMarkBox(Rect rect, Row row)
        {
            IEnumerable<ThingDef> defs;
            switch (row.kind)
            {
                case RowKind.Def:
                    defs = new[] { row.def };
                    break;
                case RowKind.Category:
                    defs = row.node.catDef.DescendantThingDefs;
                    break;
                case RowKind.Undiscovered:
                    defs = row.undiscovered;
                    break;
                default:
                    return;
            }
            MultiCheckboxState state = MarkState(defs);
            MultiCheckboxState after = Widgets.CheckboxMulti(rect, state, paintable: true);
            TooltipHandler.TipRegion(rect, "StorageOverview_MarkTip".Translate());
            if (after != state)
            {
                SetMarked(defs, after == MultiCheckboxState.On);
            }
        }

        // --------------------------------------------------------- column names

        private void DrawHeader(Rect rect, StorageColumn column)
        {
            // One character per line, bottom-aligned. Rotated text is not used: on 1.7 it
            // loses the UI scale and is clipped as if unrotated.
            Text.Anchor = TextAnchor.LowerCenter;
            if (!UsesRotatedLabel(column.label))
            {
                Widgets.Label(rect.ContractedBy(0f, 2f), VerticalText(column.label, rect.height - 4f));
            }
            Text.Anchor = TextAnchor.UpperLeft;
            string names = column.IsMerged ? column.MemberNames() : column.label;
            TooltipHandler.TipRegion(rect, ItemsOnly ? names : "StorageOverview_ColumnTip".Translate(names, column.Priority.Label()).ToString());
            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 1 && rect.Contains(e.mousePosition))
            {
                // Used on mouse down so the invisible button below never arms for it.
                e.Use();
                OpenHeaderMenu(column);
            }
            if (Widgets.ButtonInvisible(rect))
            {
                JumpTo(column);
            }
        }

        /// <summary>Right-click menu: rename, and the vanilla storage settings clipboard.</summary>
        private void OpenHeaderMenu(StorageColumn column)
        {
            var options = new List<FloatMenuOption>();
            bool renamable = column.members.Exists(m => RenameDialogFor(m.owner) != null);
            options.Add(new FloatMenuOption("Rename".Translate(), renamable ? () => OpenRename(column) : (System.Action)null));
            options.Add(new FloatMenuOption("CommandCopyZoneSettingsLabel".Translate(),
                ItemsOnly ? (System.Action)null : () => StorageSettingsClipboard.Copy(column.settings)));
            options.Add(new FloatMenuOption("CommandPasteZoneSettingsLabel".Translate(),
                !ItemsOnly && StorageSettingsClipboard.HasCopiedSettings ? () => PasteInto(column) : (System.Action)null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>Pastes into every member; the vanilla message is shown once.</summary>
        private void PasteInto(StorageColumn column)
        {
            StorageSettings first = column.members[0].settings;
            StorageSettingsClipboard.PasteInto(first);
            for (int i = 1; i < column.members.Count; i++)
            {
                column.members[i].settings.CopyFrom(first);
            }
            InvalidateStates();
        }

        /// <summary>
        /// Opens the vanilla rename dialog. Names of zones and storage groups must be unique,
        /// so a merged column first asks which member to rename.
        /// </summary>
        private void OpenRename(StorageColumn column)
        {
            if (!column.IsMerged)
            {
                Window dialog = RenameDialogFor(column.owner);
                if (dialog != null)
                {
                    renameDialog = dialog;
                    Find.WindowStack.Add(dialog);
                }
                return;
            }
            var options = new List<FloatMenuOption>();
            foreach (StorageColumn member in column.members)
            {
                IStoreSettingsParent owner = member.owner;
                if (RenameDialogFor(owner) == null)
                {
                    continue;
                }
                options.Add(new FloatMenuOption(member.label, () =>
                {
                    Window dialog = RenameDialogFor(owner);
                    renameDialog = dialog;
                    Find.WindowStack.Add(dialog);
                }));
            }
            if (options.Count > 0)
            {
                Find.WindowStack.Add(new FloatMenu(options));
            }
        }

        /// <summary>The dialog vanilla opens for this storage, or null when it cannot be renamed.</summary>
        private static Window RenameDialogFor(IStoreSettingsParent owner)
        {
            switch (owner)
            {
                case Zone zone:
                    return new Dialog_RenameZone(zone);
                case StorageGroup group:
                    return new Dialog_RenameBuildingStorage(group);
                case IStorageGroupMember member when member.ShowRenameButton:
                    return new Dialog_RenameBuildingStorage_CreateNew(member);
                default:
                    return null;
            }
        }

        /// <summary>
        /// All names are rotated (uniform look, longer names fit). The one-character-per-line
        /// fallback in VerticalText is kept in case rotation misbehaves on some version.
        /// </summary>
        private static bool UsesRotatedLabel(string label) => true;

        /// <summary>
        /// Label turned 90 degrees counter-clockwise (read bottom to top, as in spreadsheet
        /// column headers), starting at the pivot just above the cells.
        /// Two 1.7 quirks shape this: GUIUtility.RotateAroundPivot drops the UI scale, so the
        /// rotation is composed onto the current matrix here; and clipping uses the unrotated
        /// rect, so the text is laid out at a fixed spot inside the window (left edge) and the
        /// matrix moves it onto the column, keeping every column's text unclipped.
        /// </summary>
        private void DrawRotatedLabel(Vector2 pivot, string label, float length, bool dimmed)
        {
            Vector2 origin = windowRect.position + new Vector2(Margin, Margin);
            Vector2 anchor = new Vector2(0f, pivot.y);
            Vector3 target = new Vector3(pivot.x + origin.x, pivot.y + origin.y, 0f);
            Vector3 source = new Vector3(anchor.x + origin.x, anchor.y + origin.y, 0f);
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = old * Matrix4x4.TRS(target, Quaternion.Euler(0f, 0f, -90f), Vector3.one)
                * Matrix4x4.TRS(-source, Quaternion.identity, Vector3.one);
            Text.Anchor = TextAnchor.MiddleLeft;
            if (dimmed)
            {
                GUI.color = Color.gray;
            }
            Widgets.Label(new Rect(anchor.x, anchor.y - RowHeight / 2f, length, RowHeight), label.Truncate(length));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.matrix = old;
        }

        private static string VerticalText(string label, float height)
        {
            int maxLines = Mathf.Max(1, Mathf.FloorToInt(height / Text.LineHeight));
            var chars = new List<string>();
            foreach (char ch in label)
            {
                if (!char.IsWhiteSpace(ch))
                {
                    chars.Add(ch.ToString());
                }
            }
            if (chars.Count > maxLines)
            {
                chars.RemoveRange(maxLines - 1, chars.Count - maxLines + 1);
                chars.Add("…");
            }
            return string.Join("\n", chars);
        }

        /// <summary>
        /// Moves the camera so the storage sits in the middle of the map strip right of
        /// this tab.
        /// </summary>
        private void JumpTo(StorageColumn column)
        {
            // Camera only, no selection: some storage mods (Adaptive Storage Framework) open
            // the inspect pane when their building is selected, which closes this tab.
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (StorageColumn member in column.members)
            {
                if (TryGetCenter(member.owner, out Vector3 center))
                {
                    sum += center;
                    count++;
                }
            }
            if (count == 0)
            {
                return;
            }
            Vector3 target = sum / count;
            float freeCenterX = (windowRect.xMax + UI.screenWidth) / 2f;
            float offsetCells = (freeCenterX - UI.screenWidth / 2f) / Find.CameraDriver.CellSizePixels;
            Find.CameraDriver.JumpToCurrentMapLoc(target - new Vector3(offsetCells, 0f, 0f));
        }


        /// <summary>Centre of the cells or buildings one storage owner covers.</summary>
        private static bool TryGetCenter(IStoreSettingsParent owner, out Vector3 center)
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            switch (owner)
            {
                case StorageGroup group:
                    foreach (IStorageGroupMember member in group.members)
                    {
                        if (member is Thing thing && thing.Spawned)
                        {
                            sum += thing.DrawPos;
                            count++;
                        }
                    }
                    break;
                case Zone zone:
                    foreach (IntVec3 cell in zone.Cells)
                    {
                        sum += cell.ToVector3Shifted();
                        count++;
                    }
                    break;
                case Thing thing when thing.Spawned:
                    sum = thing.DrawPos;
                    count = 1;
                    break;
            }
            center = count > 0 ? sum / count : Vector3.zero;
            return count > 0;
        }

        // ------------------------------------------- priority / hit points / quality

        private static Color PriorityColor(StoragePriority priority)
        {
            switch (priority)
            {
                case StoragePriority.Critical: return new Color(1f, 0.45f, 0.4f);
                case StoragePriority.Important: return new Color(1f, 0.75f, 0.35f);
                case StoragePriority.Preferred: return new Color(0.55f, 0.8f, 1f);
                case StoragePriority.Normal: return new Color(0.85f, 0.85f, 0.85f);
                default: return new Color(0.55f, 0.55f, 0.55f);
            }
        }

        /// <summary>1-5 stacked bars (Low to Critical); click opens the vanilla priority menu.</summary>
        private static void DrawPriorityCell(Rect rect, StorageColumn column)
        {
            StoragePriority priority = column.Priority;
            int level = (int)priority;
            Color color = PriorityColor(priority);
            const float barWidth = 14f;
            const float barHeight = 2f;
            const float gap = 2f;
            float x = rect.center.x - barWidth / 2f;
            float bottom = rect.center.y + (5f * barHeight + 4f * gap) / 2f;
            for (int i = 0; i < 5; i++)
            {
                Rect bar = new Rect(x, bottom - (i + 1) * barHeight - i * gap, barWidth, barHeight);
                Widgets.DrawBoxSolid(bar, i < level ? color : new Color(1f, 1f, 1f, 0.12f));
            }
            Widgets.DrawHighlightIfMouseover(rect);
            TooltipHandler.TipRegion(rect, column.label + "\n" + "Priority".Translate().CapitalizeFirst() + ": " + priority.Label().CapitalizeFirst());
            if (Widgets.ButtonInvisible(rect))
            {
                var options = new List<FloatMenuOption>();
                foreach (StoragePriority value in System.Enum.GetValues(typeof(StoragePriority)))
                {
                    if (value != StoragePriority.Unstored)
                    {
                        StoragePriority local = value;
                        options.Add(new FloatMenuOption(local.Label().CapitalizeFirst(), () => column.Priority = local));
                    }
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
        }

        /// <summary>
        /// Vertical range bar (bottom = worst, top = best). Drag either end; clicking the
        /// bar moves the nearer end.
        /// </summary>
        private void DrawRangeCell(Rect rect, StorageColumn column, bool quality)
        {
            if (quality ? !column.qualityConfigurable : !column.hitPointsConfigurable)
            {
                return;
            }
            ThingFilter filter = column.Filter;
            float low;
            float high;
            string tip;
            if (quality)
            {
                QualityRange range = filter.AllowedQualityLevels;
                low = (int)range.min / (float)QualitySteps;
                high = (int)range.max / (float)QualitySteps;
                tip = range.min.GetLabel().CapitalizeFirst() + " - " + range.max.GetLabel().CapitalizeFirst();
            }
            else
            {
                FloatRange range = filter.AllowedHitPointsPercents;
                low = range.min;
                high = range.max;
                tip = Mathf.RoundToInt(low * 100f) + "% - " + Mathf.RoundToInt(high * 100f) + "%";
            }
            bool full = low <= 0.001f && high >= 0.999f;

            Rect track = new Rect(rect.center.x - 3f, rect.y + 8f, 6f, rect.height - 16f);
            Widgets.DrawBoxSolid(track, new Color(0.12f, 0.12f, 0.12f));
            if (quality)
            {
                for (int i = 0; i <= QualitySteps; i++)
                {
                    float tickY = track.yMax - i / (float)QualitySteps * track.height;
                    Widgets.DrawBoxSolid(new Rect(track.x - 3f, tickY - 0.5f, track.width + 6f, 1f), new Color(1f, 1f, 1f, 0.2f));
                }
            }
            float lowY = track.yMax - low * track.height;
            float highY = track.yMax - high * track.height;
            Color fill = full ? new Color(0.45f, 0.45f, 0.45f) : new Color(0.9f, 0.75f, 0.3f);
            Widgets.DrawBoxSolid(new Rect(track.x, highY, track.width, Mathf.Max(1f, lowY - highY)), fill);
            Widgets.DrawBoxSolid(new Rect(rect.center.x - 7f, highY - 1.5f, 14f, 3f), Color.white);
            Widgets.DrawBoxSolid(new Rect(rect.center.x - 7f, lowY - 1.5f, 14f, 3f), Color.white);

            string name = quality ? "Quality".Translate().CapitalizeFirst() : "HitPointsBasic".Translate().CapitalizeFirst();
            TooltipHandler.TipRegion(rect, column.label + "\n" + name + ": " + tip);

            Event e = Event.current;
            bool dragging = dragColumn == column && dragQuality == quality;
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                float mouseY = e.mousePosition.y;
                dragColumn = column;
                dragQuality = quality;
                dragMax = Mathf.Abs(mouseY - highY) < Mathf.Abs(mouseY - lowY)
                    || (Mathf.Approximately(highY, lowY) && mouseY < highY);
                SetRangeFromMouse(column, quality, track, mouseY);
                e.Use();
            }
            else if (dragging && e.type == EventType.MouseDrag)
            {
                SetRangeFromMouse(column, quality, track, e.mousePosition.y);
                e.Use();
            }
            else if (dragging && e.rawType == EventType.MouseUp)
            {
                dragColumn = null;
            }
        }

        private void SetRangeFromMouse(StorageColumn column, bool quality, Rect track, float mouseY)
        {
            float value = Mathf.Clamp01((track.yMax - mouseY) / track.height);
            ThingFilter filter = column.Filter;
            if (quality)
            {
                int step = Mathf.RoundToInt(value * QualitySteps);
                QualityRange range = filter.AllowedQualityLevels;
                if (dragMax)
                {
                    range.max = (QualityCategory)Mathf.Max(step, (int)range.min);
                }
                else
                {
                    range.min = (QualityCategory)Mathf.Min(step, (int)range.max);
                }
                column.SetQuality(range);
            }
            else
            {
                value = Mathf.Round(value * 100f) / 100f;
                FloatRange range = filter.AllowedHitPointsPercents;
                if (dragMax)
                {
                    range.max = Mathf.Max(value, range.min);
                }
                else
                {
                    range.min = Mathf.Min(value, range.max);
                }
                column.SetHitPoints(range);
            }
        }

        // ---------------------------------------------------------------- cells

        private void DrawCell(Rect rect, Row row, int columnIndex)
        {
            StorageColumn column = columns[columnIndex];
            Rect checkRect = new Rect(rect.x + (rect.width - CheckSize) / 2f, rect.y + (rect.height - CheckSize) / 2f, CheckSize, CheckSize);
            switch (row.kind)
            {
                case RowKind.Def:
                {
                    if (!column.visibleDefs.Contains(row.def))
                    {
                        return;
                    }
                    bool on = column.Filter.Allows(row.def);
                    int heldCount = HeldCount(column, row.def);
                    if (heldCount > 0)
                    {
                        DrawHeldTint(rect, true);
                        TooltipHandler.TipRegion(rect, "StorageOverview_HeldTip".Translate(column.label, row.def.LabelCap, heldCount));
                    }
                    StoragePriority? destination = DestinationPriority(row.def);
                    if (on && column.members.Exists(m => m.settings.Priority == destination))
                    {
                        // Where haulers take this item: the highest priority storage allowing it
                        // (all of them on a tie, vanilla then picks the nearest).
                        GUI.color = new Color(0.45f, 0.85f, 1f);
                        Widgets.DrawBox(rect.ContractedBy(1f), 2);
                        GUI.color = Color.white;
                    }
                    bool before = on;
                    Widgets.Checkbox(checkRect.position, ref on, CheckSize, disabled: false, paintable: true);
                    if (on != before)
                    {
                        column.SetAllow(row.def, on);
                        InvalidateStates();
                    }
                    break;
                }
                case RowKind.Special:
                {
                    if (!column.visibleSpecials.Contains(row.special))
                    {
                        return;
                    }
                    bool on = column.Filter.Allows(row.special);
                    bool before = on;
                    Widgets.Checkbox(checkRect.position, ref on, CheckSize, disabled: false, paintable: true);
                    if (on != before)
                    {
                        column.SetAllow(row.special, on);
                        InvalidateStates();
                    }
                    break;
                }
                case RowKind.Undiscovered:
                {
                    MultiCheckboxState? state = StateOf(column, row.undiscovered, null);
                    DrawGroupCell(checkRect, column, row.undiscovered, null, state);
                    break;
                }
                case RowKind.Category:
                {
                    ThingCategoryDef cat = row.node.catDef;
                    DrawHeldTint(rect, CategoryHeld(cat, columnIndex));
                    DrawGroupCell(checkRect, column, cat.DescendantThingDefs, cat.DescendantSpecialThingFilterDefs, CategoryState(cat, columnIndex));
                    break;
                }
            }
        }

        private void DrawGroupCell(Rect checkRect, StorageColumn column, IEnumerable<ThingDef> defs,
            IEnumerable<SpecialThingFilterDef> specials, MultiCheckboxState? state)
        {
            if (state == null)
            {
                return;
            }
            MultiCheckboxState after = Widgets.CheckboxMulti(checkRect, state.Value, paintable: true);
            if (after == state.Value)
            {
                return;
            }
            // Same effect as ThingFilter.SetAllow(category) in the vanilla tab: every listed
            // def and special filter below the category.
            bool allow = after == MultiCheckboxState.On;
            foreach (ThingDef def in defs)
            {
                if (column.visibleDefs.Contains(def))
                {
                    column.SetAllow(def, allow);
                }
            }
            if (specials != null)
            {
                foreach (SpecialThingFilterDef sf in specials)
                {
                    if (column.visibleSpecials.Contains(sf))
                    {
                        column.SetAllow(sf, allow);
                    }
                }
            }
            InvalidateStates();
        }

        private StoragePriority? DestinationPriority(ThingDef def)
        {
            if (!destinationPriority.TryGetValue(def, out StoragePriority? best))
            {
                best = null;
                foreach (StorageColumn column in allColumns)
                {
                    if (column.visibleDefs.Contains(def) && column.Filter.Allows(def)
                        && (best == null || column.settings.Priority > best.Value))
                    {
                        best = column.settings.Priority;
                    }
                }
                destinationPriority[def] = best;
            }
            return best;
        }

        private MultiCheckboxState? CategoryState(ThingCategoryDef cat, int columnIndex)
        {
            if (!categoryStates.TryGetValue(cat, out MultiCheckboxState?[] states))
            {
                states = new MultiCheckboxState?[columns.Count];
                for (int i = 0; i < columns.Count; i++)
                {
                    states[i] = StateOf(columns[i], cat.DescendantThingDefs, cat.DescendantSpecialThingFilterDefs);
                }
                categoryStates[cat] = states;
            }
            return states[columnIndex];
        }

        /// <summary>
        /// Same counting as Listing_TreeThingFilter.AllowanceStateOf. Null when the column
        /// can hold none of the defs (empty cell).
        /// </summary>
        private static MultiCheckboxState? StateOf(StorageColumn column, IEnumerable<ThingDef> defs,
            IEnumerable<SpecialThingFilterDef> specials)
        {
            int visible = 0;
            int allowed = 0;
            foreach (ThingDef def in defs)
            {
                if (column.visibleDefs.Contains(def))
                {
                    visible++;
                    if (column.Filter.Allows(def))
                    {
                        allowed++;
                    }
                }
            }
            if (visible == 0)
            {
                return null;
            }
            int visibleSpecial = 0;
            int allowedSpecial = 0;
            if (specials != null)
            {
                foreach (SpecialThingFilterDef sf in specials)
                {
                    if (column.visibleSpecials.Contains(sf))
                    {
                        visibleSpecial++;
                        if (column.Filter.Allows(sf))
                        {
                            allowedSpecial++;
                        }
                    }
                }
            }
            if (allowed == 0)
            {
                return MultiCheckboxState.Off;
            }
            return allowed == visible && allowedSpecial == visibleSpecial ? MultiCheckboxState.On : MultiCheckboxState.Partial;
        }
    }
}
