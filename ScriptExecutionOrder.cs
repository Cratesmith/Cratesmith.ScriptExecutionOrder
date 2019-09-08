#define SORT_EXECUTION_ORDER
#define AUTO_SCRIPT_EXECUTION_ORDER
#define SORT_ON_ASSETIMPORTER // only use if SORT_ON_SCRIPT_RELOAD is giving issues. May take a second recompile to see changes
//#define SORT_ON_SCRIPT_RELOAD
#define LOG_DEBUG
//#define LOG_DEBUG_VERBOSE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using System.Linq;
#endif

using Debug = UnityEngine.Debug;

namespace Cratesmith.ScriptExecutionOrder
{
    /// <summary>
    /// Sets the script exection order to a specific value.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class ScriptExecutionOrderAttribute : System.Attribute 
    {
        public readonly int order;
        public ScriptExecutionOrderAttribute(int order)
        {
            this.order = order;
        }
    }

    public abstract class ScriptExecuteAttribute : System.Attribute
    {
        public abstract Type RelativeTo { get; }
    }

    /// <summary>
    /// Ensures that this script will execute after all scripts of the specified type.
    /// Exection order for all scripts in a dependency chain are automatically assigned.
    /// This respects order values set by ScriptExecutionOrderAttribute, and will show a warning if that's not possible.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
    public class ScriptExecuteAfterAttribute : ScriptExecuteAttribute
    {
        public ScriptExecuteAfterAttribute(Type type)
        {
            ExecuteAfter = type;
        }   
        public Type ExecuteAfter { get; private set; }

        public override Type RelativeTo => ExecuteAfter;
    }

    /// <summary>
    /// Ensures that this script will execute before all scripts of the specified type.
    /// Exection order for all scripts in a dependency chain are automatically assigned.
    /// This respects order values set by ScriptExecutionOrderAttribute, and will show a warning if that's not possible.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
    public class ScriptExecuteBeforeAttribute : ScriptExecuteAttribute
    {
        public ScriptExecuteBeforeAttribute(Type type)
        {
            ExecuteBefore = type;
        }

        public Type ExecuteBefore { get; private set; }
        public override Type RelativeTo => ExecuteBefore;

    }
   
#if UNITY_EDITOR && AUTO_SCRIPT_EXECUTION_ORDER
    public static class ScriptExecutionOrder
    {
        const string EDITORPREFS_ScriptOrdersChanged = "ScriptExecutionOrder.ScriptOrdersChanged";
        public static bool PrefsScriptOrdersChanged
        {
            get => EditorPrefs.GetBool(EDITORPREFS_ScriptOrdersChanged, false);
            private set { EditorPrefs.SetBool(EDITORPREFS_ScriptOrdersChanged, value);}
        }

        const string EDITORPREFS_ScriptsReimported = "ScriptExecutionOrder.ScriptsReimported";
        public static bool ScriptsReimported
        {
            get => EditorPrefs.GetBool(EDITORPREFS_ScriptsReimported, false);
            private set { EditorPrefs.SetBool(EDITORPREFS_ScriptsReimported, value);}
        }
        
        const string EDITORPREFS_LastLoadTime = "ScriptExecutionOrder.LastLoadTime";
        public static float PrefsLastLoadTime
        {
            get => EditorPrefs.GetFloat(EDITORPREFS_LastLoadTime, 0f);
            private set { EditorPrefs.SetFloat(EDITORPREFS_LastLoadTime, value);}
        }

        [InitializeOnLoadMethod]
        static void CheckForEditorStartup()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode
                || EditorApplication.timeSinceStartup > PrefsLastLoadTime)
            {
                return;
            }

            PrefsScriptOrdersChanged = false;
            PrefsLastLoadTime = (float)EditorApplication.timeSinceStartup;
#if !SORT_ON_SCRIPT_RELOAD
        ProcessAll();
#endif
        }

#if SORT_ON_ASSETIMPORTER
        public class Builder : UnityEditor.AssetPostprocessor
        {
            static void OnPostprocessAllAssets (string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths) 
            {
                ScriptsReimported = false;

                if(importedAssets
                    .Concat(movedAssets)
                    .Concat(deletedAssets)
                    .Any(x=> AssetImporter.GetAtPath(x) is MonoImporter))
                {
                    if (PrefsScriptOrdersChanged)
                    {
                        PrefsScriptOrdersChanged = false;
                        Debug.Log("ScriptExecutionOrder: Reloading scripts after an order change, not sorting in case we're in a loop");
                        return;
                    }
                    
                    ScriptsReimported = true;
                }
            }
        }
        
        [UnityEditor.Callbacks.DidReloadScripts(-80)]
        static void ScriptReload()
        {
            if (!ScriptsReimported) return;
            ProcessAll();
            ScriptsReimported = false;
        }
        
#elif SORT_ON_SCRIPT_RELOAD
        [UnityEditor.Callbacks.DidReloadScripts(-80)]
        static void ScriptReload()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            if (PrefsScriptOrdersChanged)
            {
                PrefsScriptOrdersChanged = false;
                Debug.Log("ScriptExecutionOrder: Reloading scripts after an order change, not sorting in case we're in a loop");
                return;
            }

            ProcessAll();
        }
#endif

        static bool CheckIfNeedsSort()
        {
            var allScripts = new List<MonoScript>(MonoImporter.GetAllRuntimeMonoScripts());
            var scriptOrders = new Dictionary<Type, int>();
            foreach (var script in allScripts)
            {
                if (!script) continue;
                var scriptClass = script.GetClass();
                if (scriptClass==null) continue;
                var order = MonoImporter.GetExecutionOrder(script);
                scriptOrders[scriptClass] = order;
            }
       
            foreach (var script in allScripts)
            {
                if (!script) continue;
                var scriptClass = script.GetClass();
                if (scriptClass==null) continue;
                if(!scriptOrders.TryGetValue(scriptClass, out var order)) continue;
            
                var fixedOrderAttribute =
                    scriptClass.GetCustomAttributes(typeof(ScriptExecutionOrderAttribute), true)
                        .Cast<ScriptExecutionOrderAttribute>()
                        .FirstOrDefault();
                if (fixedOrderAttribute != null)
                {
                    if (order != fixedOrderAttribute.order) return true;
                }

                var usedTypes = new HashSet<Type>();
                var attributes = scriptClass.GetCustomAttributes(typeof(ScriptExecuteAttribute), true).ToArray();
                foreach (ScriptExecuteAttribute attribute in attributes)
                {
                    if (!usedTypes.Add(attribute.RelativeTo)) continue;

                    if (attribute is ScriptExecuteAfterAttribute afterAttrib)
                    {
                        if (!scriptOrders.TryGetValue(afterAttrib.ExecuteAfter, out var otherOrder)) continue;
                        if (order <= otherOrder) return true;
                    }
                
                    if (attribute is ScriptExecuteBeforeAttribute beforeAttribute)
                    {
                        if (!scriptOrders.TryGetValue(beforeAttribute.ExecuteBefore, out var otherOrder)) continue;
                        if (order >= otherOrder) return true;
                    }
                }
            }
            return false;
        }
    
        [MenuItem("Tools/Script ExecutionOrder/Trigger Auto Sort")]
        static void ProcessAll()
        {
        
            var stopwatch = new Stopwatch();
            stopwatch.Start();
#if LOG_DEBUG
            Debug.Log("ScriptExecutionOrder: Starting sort");
#endif

            if (!CheckIfNeedsSort())
            {
                stopwatch.Stop();
#if LOG_DEBUG
                Debug.Log($"ScriptExecutionOrder: Doesn't need to sort. Took {stopwatch.Elapsed.TotalSeconds.ToString("F2")}s");
#endif
                return;
            }
        
            var fixedOrders = new Dictionary<UnityEditor.MonoScript, int>();
            var allScripts = new List<UnityEditor.MonoScript>();
            foreach (var script in MonoImporter.GetAllRuntimeMonoScripts())
            {           
                if(!script || script.GetClass()==null) continue;
                int newOrder = 0;
                if(GetFixedOrder(script, out newOrder))
                {
                    fixedOrders[script] = newOrder;
                }
                allScripts.Add(script);
            }
             
            var scriptOrders = new Dictionary<UnityEditor.MonoScript, int>();
            var sortedDeps = SortDependencies(allScripts.ToArray());
            for(int i=0;i<sortedDeps.Count; ++i)
            {
                bool hasFixedOrderItem = false;

                //
                // find out the starting priority for this island 
                var currentIsland = sortedDeps[i];

                var newDepOrder = -currentIsland.Length;
                for(int j=0; j<currentIsland.Length; ++j)
                {
                    var script = currentIsland[j].script;
                    int scriptOrder = 0;
                    if(fixedOrders.TryGetValue(script, out scriptOrder))
                    {
                        // -j due to sorted items before it
                        newDepOrder = Mathf.Min(scriptOrder-j, newDepOrder);
                        hasFixedOrderItem = true;
                    }
                }

                //
                // Don't edit execution order unless there's a fixed order or a dependency
                // This allows the script exection order UI to work normally for these cases 
                // instead of forcing them to exection order 0
                if(currentIsland.Length==1 && !hasFixedOrderItem)
                {
                    continue;
                }

#if LOG_DEBUG            
                Debug.Log("ScriptExecutionOrder: Island:"+i+" starts at "+newDepOrder
                          +" Scripts:"+string.Join(", ", currentIsland
                              .Select(x=>(fixedOrders.ContainsKey(x.script) 
                                             ? (x.script.name+"[fixed="+fixedOrders[x.script]+"]")
                                             : x.script.name)+"(isLeaf="+x.isLeaf+")")
                              .ToArray()));
#endif


                // 
                // apply priorities in order
                for(int j=0; j<currentIsland.Length; ++j)  
                {               
                    int scriptFixedOrder = 0;
                    var script = currentIsland[j].script;
                    var isLeaf = currentIsland[j].isLeaf;

                    if(fixedOrders.TryGetValue(script, out scriptFixedOrder))
                    {
                        newDepOrder = Mathf.Max(scriptFixedOrder, newDepOrder);
                        if(newDepOrder!=scriptFixedOrder)
                        {
                            Debug.LogWarning("ScriptExectionOrder: "+script.name+" has fixed exection order "+scriptFixedOrder+" but due to ScriptDependency sorting is now at order "+newDepOrder);
                        }
                        fixedOrders.Remove(script);
                    } 
                    else if(fixedOrders.Count==0)
                    {                  
                        // Try to put the leaves on script order 0 if possible, and keep others in the range [-currentIsland.Lenght,0]
                        // This just keeps the script execution order window clean and readable and avoids outliers
                        // TODO: improve this by calculating the real range of the island instead of using -currentIsland.Length
                        newDepOrder = isLeaf && !currentIsland.Skip(j+1).Any(x=>x.isLeaf)
                            ? Mathf.Max(0, newDepOrder) 
                            : Mathf.Max(-currentIsland.Length, newDepOrder);
                    }   

                    scriptOrders[script] = newDepOrder;

                    // Leaves have no dependencies so the next behaviour can share the same execution order as a leaf
                    // Also scripts with no dependencies don't need to update the order
                    // FIXME: this won't work correctly for multiple fixed order items that depend on each other!
                    var nextScript = (j+1) < currentIsland.Length ? currentIsland[j + 1].script : null;
                    if(nextScript!=null && HasDependencies(nextScript) && !isLeaf && !HasFixedOrder(nextScript))
                    { 
                        ++newDepOrder;
                    }
                }
            }

            bool scriptOrderChanged = false;
            foreach(var i in scriptOrders)
            {
                var script = i.Key;
                var order = i.Value;
                var currentOrder = UnityEditor.MonoImporter.GetExecutionOrder(script);
                if(order != currentOrder && script != null) 
                {
                    try
                    {
                        scriptOrderChanged = true;
                        Debug.LogFormat("ScriptExecutionOrder: Order changed. Script:{0} PrevOrder:{1} NewOrder:{2}", script.name, currentOrder, order);
                        UnityEditor.MonoImporter.SetExecutionOrder(script, order);
                    }
                    catch (Exception e)
                    {                    
                        Debug.LogErrorFormat("ScriptExecutionOrder: :{0} Order:{1} Error:{2}", script!=null ? script.name:"<null>", order, e);
                    }
                }
            }

            if (scriptOrderChanged)
            {
                Debug.Log("ScriptExecutionOrder: One or more orders changed. Unity will trigger a recompile");
                PrefsScriptOrdersChanged = true;
            }
        
            stopwatch.Stop();
#if LOG_DEBUG
            Debug.Log($"ScriptExecutionOrder: Sort complete. Took {stopwatch.Elapsed.TotalSeconds.ToString("F2")}s");
#endif
        }    

        /// <summary>
        /// Sort the scripts by dependencies 
        /// </summary>
        static List<IslandItem[]> SortDependencies(UnityEditor.MonoScript[] scriptsToSort)
        {
            var lookup = new Dictionary<Type, UnityEditor.MonoScript>();
            var visited = new HashSet<UnityEditor.MonoScript>();
            var sortedItems = new List<UnityEditor.MonoScript>();
            var connections = new Dictionary<UnityEditor.MonoScript, HashSet<UnityEditor.MonoScript>>();

            // sort input (to ensure we're deterministic and to ensure order of fixed items)
            Array.Sort(scriptsToSort, (x, y) => {
                var xOrder = 0; GetFixedOrder(x, out xOrder);
                var yOrder = 0; GetFixedOrder(y, out yOrder);

                var result = xOrder.CompareTo(yOrder);
                if (result != 0) return result;

                return x.GetHashCode().CompareTo(y.GetHashCode());
            });

            // add everything to lookup
            for(int i=0;i<scriptsToSort.Length;++i)
            {
                var script = scriptsToSort[i];
                if(script==null || script.GetClass()==null) continue;
                lookup[script.GetClass()] = script;
            }

            // build connection graph
            for (int i = 0; i < scriptsToSort.Length; ++i)
            {
                var script = scriptsToSort[i];
                if(script==null) continue;
                if(!HasDependencies(script)) continue;
                var deps = GetScriptDependencies(scriptsToSort[i]);
                foreach(var depType in deps)
                {
                    if(depType==null) continue;

                    MonoScript depScript = null;
                    if(!lookup.TryGetValue(depType, out depScript)) continue;

                    // forward
                    HashSet<UnityEditor.MonoScript> forwardSet = null;
                    if (!connections.TryGetValue(script, out forwardSet))
                    {
                        connections[script] = forwardSet = new HashSet<MonoScript>();
                    }
                    forwardSet.Add(depScript);
                
                    // reverse
                    HashSet<UnityEditor.MonoScript> reverseSet = null;
                    if (!connections.TryGetValue(depScript, out reverseSet))
                    {
                        connections[depScript] = reverseSet = new HashSet<MonoScript>();
                    }
                    reverseSet.Add(script);
                }           
            }

            // sort fixed order items first
            for(int i=0;i<scriptsToSort.Length;++i) 
            {
                var script = scriptsToSort[i];
                if(script==null) continue;
                if(!HasFixedOrder(script)) continue;            
                SortDependencies_Visit(script, visited, sortedItems, lookup, null, connections);
            }
		 
            // non-leaves 
            for(int i=0;i<scriptsToSort.Length;++i) 
            {
                var script = scriptsToSort[i];
                if(script==null || !HasDependencies(script)) continue;

                HashSet<MonoScript> connectionSet = null;
                if (connections.TryGetValue(script, out connectionSet) 
                    && GetScriptDependencies(script).Count == connections[script].Count)
                {
                    continue;               
                }

                SortDependencies_Visit(script, visited, sortedItems, lookup, null, connections);
            }

            // leaves (any that remain)
            for(int i=0;i<scriptsToSort.Length;++i) 
            {
                var script = scriptsToSort[i];
                if(script==null || !HasDependencies(script)) continue;
                SortDependencies_Visit(script, visited, sortedItems, lookup, null, connections);
            }
         

            //Debug.Log("ScriptExecutionOrder: Sorted dependencies: "+string.Join(", ",sortedItems.Select(x=>x.name).ToArray()));
            return SortDependencies_CreateGraphIslands(sortedItems, connections);
        }

        struct IslandItem
        {
            public UnityEditor.MonoScript script;
            public bool isLeaf;
        }

        /// <summary>
        /// Create graph islands from the non directed dependency graph
        /// </summary>
        static List<IslandItem[]> SortDependencies_CreateGraphIslands(List<UnityEditor.MonoScript> scriptsToSort, 
            Dictionary<UnityEditor.MonoScript, HashSet<UnityEditor.MonoScript>> connections)
        {
            var output = new List<IslandItem[]>(); 
            var remainingSet = new List<UnityEditor.MonoScript>(scriptsToSort);
            var leaves = new HashSet<UnityEditor.MonoScript>();

            while (remainingSet.Any())
            {
                var graphComponentSet = new HashSet<UnityEditor.MonoScript>();
                var openSet = new HashSet<UnityEditor.MonoScript>();
                var current = remainingSet.First();
                while (current != null)
                {
                    openSet.Remove(current);
                    remainingSet.Remove(current);
                    graphComponentSet.Add(current);
                    HashSet<UnityEditor.MonoScript> currentConnections = null;
                    if (connections.TryGetValue(current, out currentConnections))
                    {
                        foreach (var connection in currentConnections)
                        {
                            // leaves are scripts that no other scripts depend on
                            if(GetScriptDependencies(current).Count == currentConnections.Count)
                            {
                                leaves.Add(current);
                            }

                            if (graphComponentSet.Contains(connection))
                                continue;
                            openSet.Add(connection);
                        }
                    }
                    current = openSet.FirstOrDefault();
                }

                if (graphComponentSet.Count > 0)
                {
                    var newIsland = scriptsToSort
                        .Where(graphComponentSet.Contains)
                        .Select(x=> new IslandItem() {script = x, isLeaf = leaves.Contains(x)})
                        .ToArray();
                    output.Add(newIsland);
                }
            }

            return output;
        }

        /// <summary>
        /// Visit this script and all dependencies (adding them recursively to the sorted list).
        /// This also builds a connections table that can be used as a nondirected graph of dependencies
        /// </summary>
        static void SortDependencies_Visit( UnityEditor.MonoScript current,
            HashSet<UnityEditor.MonoScript> visited,
            List<UnityEditor.MonoScript> sortedItems,
            Dictionary<Type, UnityEditor.MonoScript> lookup,
            UnityEditor.MonoScript visitedBy,
            Dictionary<UnityEditor.MonoScript, HashSet<UnityEditor.MonoScript>> connections
        )
        {            
            if(visited.Add(current))  
            {  
                //
                // visit all dependencies (adding them recursively to the sorted list) before adding ourselves to the sorted list
                // this ensures that
                // 1. all dependencies are sorted
                // 2. cyclic dependencies can be caught if an item is visited AND it's been added to this list
                var depsRemaining = GetScriptDependencies(current);
             
                var visitedFrom = current;

                // do deps with fixed orders first
                foreach (var dep in depsRemaining)
                {
                    SortDependencies_Visit_VisitDependency(visited, sortedItems, lookup, connections, dep, visitedFrom,
                        true,
                        false);
                }

                // then non-leaves
                foreach (var dep in depsRemaining)
                {
                    SortDependencies_Visit_VisitDependency(visited, sortedItems, lookup, connections, dep, visitedFrom,
                        false,
                        false);
                }

                // then any leaves
                foreach (var dep in depsRemaining)
                {
                    SortDependencies_Visit_VisitDependency(visited, sortedItems, lookup, connections, dep, visitedFrom,
                        false,
                        true);
                }

#if LOG_DEBUG_VERBOSE
            Debug.Log("Sorted "+current.name);
#endif
                sortedItems.Add( current ); 
            } 
            else
            {
                Debug.Assert(sortedItems.Contains(current), "Cyclic dependency found for ScriptDependency "+current.name+" via "+(visitedBy!=null?visitedBy.name:"Unknown")+"!");
            }
        }

        private static void SortDependencies_Visit_VisitDependency(HashSet<MonoScript> visited, List<MonoScript> sortedItems, Dictionary<Type, MonoScript> lookup,
            Dictionary<MonoScript, HashSet<MonoScript>> connections, Type depType, MonoScript visitedFrom, bool fixedOrderDeps, bool leafDeps)
        {
            if (depType == null) return;
        
            if (!typeof(ScriptableObject).IsAssignableFrom(depType) &&
                !typeof(MonoBehaviour).IsAssignableFrom(depType))
            {
                return;
            }

            MonoScript depScript = null;                   
            if (!lookup.TryGetValue(depType, out depScript))
            {
                Debug.LogError("ScriptDependency type " + depType.Name + " not found found for script " + visitedFrom.name +
                               "! Check that it exists in a file with the same name as the class");
                return;
            }

            if (fixedOrderDeps && !HasFixedOrder(depScript))
            {
                return;
            }

            HashSet<MonoScript> connectionSet = null;
            if (leafDeps 
                && connections.TryGetValue(depScript, out connectionSet)
                && GetScriptDependencies(depScript).Count != connectionSet.Count)
            {
                return;               
            }

            if (lookup.TryGetValue(depType, out depScript)/* && !HasFixedOrder(depScript)*/)
            {
                SortDependencies_Visit(depScript, visited, sortedItems, lookup, visitedFrom, connections);
            }
        }

        /// <summary>
        /// Does this script have dependencies?
        /// </summary>
        static bool HasDependencies(UnityEditor.MonoScript script)
        {
            return GetScriptDependencies(script).Count > 0;
        }

        /// <summary>
        /// Does this script have fixed order?
        /// </summary>
        static bool HasFixedOrder(UnityEditor.MonoScript script)
        {
            int output = 0;
            return GetFixedOrder(script, out output);
        }

        /// <summary>
        /// Get the dependencies for a script using the lookup table
        /// </summary>
        private static Dictionary<MonoScript, HashSet<Type>> s_scriptDependencies = null;
        private static Dictionary<Type, Type[]> s_compatibleTypes = null;
        private static Dictionary<Type, MonoScript> s_scriptLookup = null;

        static HashSet<Type> GetScriptDependencies( UnityEditor.MonoScript script)
        {
            if(script==null) return new HashSet<Type>();

            var currentType = script.GetClass();
            if(currentType==null) return new HashSet<Type>();

            if (s_compatibleTypes == null || s_scriptDependencies == null)
            {
                var scriptsToSort = MonoImporter.GetAllRuntimeMonoScripts()
                    .Where(x => x != null && x.GetClass()!=null)
                    .GroupBy(x=>x.GetClass())
                    .Select(x=>x.FirstOrDefault())
                    .ToArray();

                if (s_compatibleTypes == null)
                {
                    s_compatibleTypes = scriptsToSort.ToDictionary(
                        x => x.GetClass(), 
                        x => scriptsToSort.Select(y=>y.GetClass()).Where(x.GetClass().IsAssignableFrom).ToArray()); 
                }

                if (s_scriptLookup == null)
                {
                    s_scriptLookup = scriptsToSort.ToDictionary(x=>x.GetClass(), x=>x);
                }

                if (s_scriptDependencies == null)
                {
                    s_scriptDependencies = scriptsToSort.ToDictionary(
                        x => x,
                        y => new HashSet<Type>());

                    foreach (var currentScript in scriptsToSort)
                    {
                        var usedTypes = new HashSet<Type>();
                        var currentClass = currentScript.GetClass();
                        var currentDeps = s_scriptDependencies[currentScript];
                        foreach (ScriptExecuteAttribute attribute in currentClass.GetCustomAttributes(
                            typeof(ScriptExecuteAttribute), true))
                        {
                            if (!usedTypes.Add(attribute.RelativeTo)) continue;

                            if(attribute is ScriptExecuteAfterAttribute afterAttrib)
                            {
                                currentDeps.Add(afterAttrib.ExecuteAfter);
                            }

                            if (attribute is ScriptExecuteBeforeAttribute beforeAttrib)
                            {
                                if (!s_scriptLookup.TryGetValue(beforeAttrib.ExecuteBefore, out var beforeScript))
                                {
                                    Debug.LogWarning($"ScriptExecutionOrder: could not find script for {beforeAttrib.ExecuteBefore.FullName} used in ScriptExecuteBefore for {currentScript.name}");
                                    continue;
                                }
                                s_scriptDependencies[beforeScript].Add(currentClass);
                            }
                        }
                    }
                }
            }

            if (s_scriptDependencies.TryGetValue(script, out var output))
            {
                return output;
            }
            return new HashSet<Type>();
        }

        private static Dictionary<MonoScript, int?> s_fixedOrderAttributes = new Dictionary<MonoScript, int?>();
        static bool GetFixedOrder(MonoScript script, out int output)
        {
            output = 0;
            if (script == null) return false;

            int? value = null;

            if (!s_fixedOrderAttributes.TryGetValue(script, out value))
            {
                var order = UnityEditor.MonoImporter.GetExecutionOrder(script);
                output = order;

                var attrib = script.GetClass().GetCustomAttributes(typeof(ScriptExecutionOrderAttribute), true).Cast<ScriptExecutionOrderAttribute>().FirstOrDefault();
                if (attrib == null)
                {
                    s_fixedOrderAttributes[script] = null;
                }
                else
                {
                    s_fixedOrderAttributes[script] = value = attrib.order;    
                }            
            }

            if (value.HasValue)
            {
                output = value.Value;
                return true;
            }

            return false;
        }
    }
#endif
}