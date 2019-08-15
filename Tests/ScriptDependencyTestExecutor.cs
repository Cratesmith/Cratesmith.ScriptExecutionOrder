using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Cratesmith.ScriptExecutionOrder.Tests
{
    [TestFixture]
    public class ScriptDependencyTestExecutor
    {
        public class ScriptData
        {
            public UnityEditor.MonoScript script;
            public int? fixedOrderValue;
            public List<ScriptData> dependsOn = new List<ScriptData>();
            public List<ScriptData> dependedOnBy = new List<ScriptData>();
        }

        Dictionary<System.Type, UnityEditor.MonoScript> typeLookup =
            new Dictionary<System.Type, UnityEditor.MonoScript>();

        Dictionary<UnityEditor.MonoScript, ScriptData> scriptDataTable =
            new Dictionary<UnityEditor.MonoScript, ScriptData>();

        ScriptData Init_Script(UnityEditor.MonoScript script)
        {
            if (script == null)
                throw new System.ArgumentNullException("Init_Script: script cannot be null");

            var scriptClass = script.GetClass();
            if (scriptClass == null)
                throw new System.ArgumentNullException("Init_Script: must be a monoscript with a valid class");

            ScriptData scriptData = null;
            if (scriptDataTable.TryGetValue(script, out scriptData))
            {
                return scriptData;
            }
            else
            {
                scriptData = scriptDataTable[script] = new ScriptData();
                scriptData.script = script;
            }


            var fixedOrderAttribute =
                scriptClass.GetCustomAttributes(typeof(ScriptExecutionOrderAttribute), true)
                    .Cast<ScriptExecutionOrderAttribute>()
                    .FirstOrDefault();
            if (fixedOrderAttribute != null)
            {
                scriptData.fixedOrderValue = fixedOrderAttribute.order;
            }

            foreach (ScriptExecuteAfterAttribute i in scriptClass.GetCustomAttributes(typeof(ScriptExecuteAfterAttribute), true))
            {
                if (!typeLookup.TryGetValue(i.ExecuteAfter, out var j)) continue;
                var dependsOnSD = Init_Script(j);
                dependsOnSD.dependedOnBy.Add(scriptData);
                scriptData.dependsOn.Add(dependsOnSD);
            }

            foreach (ScriptExecuteBeforeAttribute i in scriptClass.GetCustomAttributes(typeof(ScriptExecuteBeforeAttribute), true))
            {
                if (!typeLookup.TryGetValue(i.ExecuteBefore, out var j)) continue;
                var dependedOnBySD = Init_Script(j);
                dependedOnBySD.dependsOn.Add(scriptData);
                scriptData.dependedOnBy.Add(dependedOnBySD);
            }

            return scriptData;
        }

        [SetUp]
        public void Init()
        {
            var allScripts = UnityEditor.MonoImporter.GetAllRuntimeMonoScripts();
            foreach (var script in allScripts)
            {
                if (script == null) continue;
                var scriptClass = script.GetClass();
                if (scriptClass == null) continue;
                typeLookup[scriptClass] = script;
            }

            foreach (var script in allScripts)
            {
                if (script == null) continue;
                var scriptClass = script.GetClass();
                if (scriptClass == null) continue;
                Init_Script(script);
            }
        }

        [TearDown]
        public void Close()
        {
            scriptDataTable = new Dictionary<UnityEditor.MonoScript, ScriptData>();
            typeLookup = new Dictionary<System.Type, UnityEditor.MonoScript>();
        }

        [Test]
        public void AllFixedAtSetExecutionOrderUnlessShifted()
        {
            foreach (var i in scriptDataTable.Values)
            {
                if (!i.fixedOrderValue.HasValue) continue;
                if (i.dependsOn.Count > 0) continue; // for now ignore any that MIGHT be shifted.
                Assert.AreEqual(i.fixedOrderValue.Value, UnityEditor.MonoImporter.GetExecutionOrder(i.script), i.script.name);
            }
        }

        [Test]
        public void AllScriptsAfterDependencies()
        {
            foreach (var scriptData in scriptDataTable.Values)
            {
                var scriptEO = UnityEditor.MonoImporter.GetExecutionOrder(scriptData.script);
                foreach (var dependsOn in scriptData.dependsOn)
                {
                    var depEO = UnityEditor.MonoImporter.GetExecutionOrder(dependsOn.script);
                    Assert.Greater(scriptEO, depEO,
                        scriptData.script.name + " order(" + scriptEO + ") must be greater than " +
                        dependsOn.script.name + " order(" + depEO + ")");
                }
            }
        }
    }
}