using System.Collections.Generic;
using Cratesmith.Utils;
using UnityEngine;
using Type = System.Type;

#if UNITY_EDITOR
using UnityEditor;
using System.Linq;
#endif

namespace Cratesmith.ScriptExecutionOrder
{
    /// <summary>
    /// Cache for ComponentDependencyAttribute so GetAttributes doesn't need to be called on each type 
    /// at runtime (potential GC Alloc and performance spikes)
    /// </summary>
    [ResourceDirectory("Assets/Plugins/cratesmith.scriptexecutionorder")]
    public class ScriptExecutionOrderCache : ResourceSingleton<ScriptExecutionOrderCache>
        , ISerializationCallbackReceiver
    {   
        [System.Serializable]
        public struct SerializedItem 
        {
            public string typeName;
            public int executionOrder;
        }  
        /// <summary>
        /// Serialized version of dependency table to be loaded at runtime.
        /// </summary>
        [SerializeField] List<SerializedItem> m_serializedItems = new List<SerializedItem>();

        /// <summary>
        /// Dependencies table for all types using ComponentDepenencyAttribute
        /// </summary>
        Dictionary<Type, int> m_executionOrder = new Dictionary<Type, int>();


        public static int GetExecutionOrder(Type forType)
        {
            int output = 0;
            instance.m_executionOrder.TryGetValue(forType, out output);
            return output;
        }
    
#if UNITY_EDITOR
        public override void OnRebuildInEditor()
        {
            // execution order will be correct given that ScriptExecutionOrder is processed at DidReloadScripts -999 and 
            // Resource singletons are built at (or after) DidReloadScripts -100
            ProcessDependencies();
        }

        private static void ProcessDependencies()
        { 
            var so = new SerializedObject(instance);

            var types = new[] { ".cs", ".js" };

            var allScriptPaths = 
                AssetDatabase.GetAllAssetPaths()
                    .Where(s => types.Any(x => s.EndsWith(x, System.StringComparison.CurrentCultureIgnoreCase)))
                    .ToArray();

            instance.m_serializedItems.Clear();

            for (int i = 0; i < allScriptPaths.Length; ++i)
            {
                MonoScript script = AssetDatabase.LoadAssetAtPath(allScriptPaths[i], typeof(MonoScript)) as MonoScript;

                if (!script || script.GetClass() == null) continue;

                var type = script.GetClass();
                if (!typeof(Component).IsAssignableFrom(script.GetClass())
                    && !typeof(ScriptableObject).IsAssignableFrom(script.GetClass()))
                {
                    continue;
                }

                var typeExecutionOrder = MonoImporter.GetExecutionOrder(script);
                if (typeExecutionOrder == 0)
                {
                    continue;
                }

                instance.m_serializedItems.Add(new SerializedItem()
                {
                    typeName = type.FullName,
                    executionOrder = typeExecutionOrder,
                });
            }

            so.Update();
            instance.hideFlags = HideFlags.NotEditable;
            EditorUtility.SetDirty(instance);
            AssetDatabase.Refresh();      
        }
#endif

        #region ISerializationCallbackReceiver implementation
        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            m_executionOrder.Clear();
            for(int i=0;i<m_serializedItems.Count;++i)
            {
                var item = m_serializedItems[i];
                if(string.IsNullOrEmpty(item.typeName)) continue;

                var forType = GetType(item.typeName);
                if(forType==null)
                {
                    continue; 
                }

                m_executionOrder[forType] = item.executionOrder;
            }
        }    
        #endregion
        static Type GetType(string name)
        { 
            Type type = null;
            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(name);
                if (type != null) break;
            }
            return type;
        }
    }
}
