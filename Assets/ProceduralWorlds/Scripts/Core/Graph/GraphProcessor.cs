﻿using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq;
using System;
using UnityEngine;
using UnityEngine.Profiling;

using Debug = UnityEngine.Debug;
using NodeFieldDictionary = System.Collections.Generic.Dictionary< string, System.Collections.Generic.Dictionary< string, System.Reflection.FieldInfo > >;

namespace ProceduralWorlds.Core
{
	public class BaseGraphProcessor
	{

		public delegate object	AtGenericDelegate(IPWArray array, int index);
		public delegate bool	AssignAtGenericDelegate(IPWArray array, int index, object val, string name, bool force);

		readonly NodeFieldDictionary	bakedNodeFields = new NodeFieldDictionary();
		Dictionary< int, BaseNode >		nodesDictionary;

		BaseGraph						currentGraph;

		public bool						hasProcessed;

		#region Initialization

		static object GenericAt(IPWArray array, int index)
		{
			return array.At(index);
		}

		static bool GenericAssignAt(IPWArray array, int index, object val, string name, bool force)
		{
			return array.GenericAssignAt(index, val, name, force);
		}
		
		void BakeNode(System.Type t)
		{
			var dico = new Dictionary< string, FieldInfo >();
			bakedNodeFields[t.AssemblyQualifiedName] = dico;
	
			foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
			{
				var attrs = field.GetCustomAttributes(false);

				bool skip = true;

				foreach (var attr in attrs)
					if (attr is InputAttribute || attr is OutputAttribute)
						skip = false;
				
				if (skip)
					continue ;
				
				dico[field.Name] = field;
			}
		}
	
		public void OnEnable()
		{
			//bake node fields to accelerate data transfer from node to node.
			bakedNodeFields.Clear();
			foreach (var nodeType in NodeTypeProvider.GetAllNodeTypes())
				BakeNode(nodeType);
		}
	
		#endregion

		#region Processing

		//Check errors when transferring values from a node to another
		bool CheckProcessErrors(NodeLink link, BaseNode node, bool realMode)
		{
			if (!realMode)
			{
				if (link.fromAnchor == null || link.toAnchor == null)
				{
					Debug.LogError("[PW Process] null anchors in link: " + link + ", from node: " + node + ", trying to removing this link");
					currentGraph.RemoveLink(link, false);
					return true;
				}
				
				var fromType = Type.GetType(link.fromNode.classAQName);
				var toType = Type.GetType(link.toNode.classAQName);

				if (!nodesDictionary.ContainsKey(link.toNode.id))
				{
					Debug.LogError("[PW Process] " + "node id (" + link.toNode.id + ") not found in nodes dictionary");
					return true;
				}

				if (nodesDictionary[link.toNode.id] == null)
				{
					Debug.LogError("[PW Process] " + "node id (" + link.toNode.id + ") is null in nodes dictionary");
					return true;
				}

				if (!bakedNodeFields.ContainsKey(link.fromNode.classAQName)
					|| !bakedNodeFields[link.fromNode.classAQName].ContainsKey(link.fromAnchor.fieldName))
				{
					Debug.LogError("[PW Process] Can't find field: "
						+ link.fromAnchor.fieldName + " in " + fromType);
					return true;
				}

				if (!bakedNodeFields.ContainsKey(link.toNode.classAQName)
					|| !bakedNodeFields[link.toNode.classAQName].ContainsKey(link.toAnchor.fieldName))
				{
					Debug.LogError("[PW Process] Can't find field: "
						+ link.toAnchor.fieldName + " in " + toType);
					return true;
				}
				
				if (bakedNodeFields[link.fromNode.classAQName][link.fromAnchor.fieldName].GetValue(node) == null)
				{
					if (currentGraph.processMode != GraphProcessMode.Once)
						Debug.Log("[PW Process] tring to assign null value from "
							+ fromType + "." + link.fromAnchor.fieldName);
					return true;
				}
			}

			return false;
		}
		
		void TrySetValue(FieldInfo field, object val, BaseNode target, bool realMode)
		{
			if (realMode)
				field.SetValue(target, val);
			else
				try {
					field.SetValue(target, val);
				} catch (Exception e) {
					Debug.LogError("[BaseGraph Processor] " + e);
				}
		}
	
		void ProcessNodeLinks(BaseNode node, bool realMode)
		{
			var links = node.GetOutputLinks();

			if (!realMode)
				Profiler.BeginSample("[PW] Process node links " + node);

			foreach (var link in links)
			{
				//if we are in real mode, we check all errors and discard if there is any.
				if (CheckProcessErrors(link, node, realMode))
					return ;

				var val = bakedNodeFields[link.fromNode.classAQName][link.fromAnchor.fieldName].GetValue(node);
				var prop = bakedNodeFields[link.toNode.classAQName][link.toAnchor.fieldName];

				//Without multi-anchor, simple assignation
				if (link.toAnchor.fieldIndex == -1 && link.fromAnchor.fieldIndex == -1)
					TrySetValue(prop, val, link.toNode, realMode);
				
				//Distant anchor is a multi-anchor
				else if (link.toAnchor.fieldIndex != -1 && link.fromAnchor.fieldIndex == -1)
				{
					var pwArray = prop.GetValue(link.toNode);

					bool b = GenericAssignAt(pwArray as IPWArray, link.toAnchor.fieldIndex, val, link.fromAnchor.name, true);

					if (!b)
						Debug.LogError("[BaseGraph Processor] Failed to set distant indexed field value: " + link.toAnchor.fieldName + " at index: " + link.toAnchor.fieldIndex);
				}

				//Local link is a multi-anchor
				else if (link.toAnchor.fieldIndex == -1 && link.fromAnchor.fieldIndex != -1 && val != null)
				{
					object localVal = GenericAt(val as IPWArray, link.fromAnchor.fieldIndex);

					TrySetValue(prop, localVal, link.toNode, realMode);
				}

				//Both are multi-anchors
				else if (val != null)
				{
					object localVal = GenericAt(val as IPWArray, link.fromAnchor.fieldIndex);
	
					var pwArray = prop.GetValue(link.toNode);
					bool b = GenericAssignAt(pwArray as IPWArray, link.toAnchor.fieldIndex, localVal, link.fromAnchor.name, true);
					
					if (!b)
						Debug.Log("[BaseGraph Processor] Failed to set distant indexed field value: " + link.toAnchor.fieldName);
				}
			}

			if (!realMode)
				Profiler.EndSample();
		}
	
		float ProcessNode(BaseNode node, bool realMode)
		{
			float	calculTime = 0;

			//if you are in editor mode, update the process time of the node
			if (!realMode)
			{
				Profiler.BeginSample("[PW] Process node " + node);
				Stopwatch	st = new Stopwatch();

				st.Start();
				node.Process();
				st.Stop();

				node.processTime = (float)st.Elapsed.TotalMilliseconds;
				calculTime = node.processTime;
			}
			else
				node.Process();

			ProcessNodeLinks(node, realMode);

			if (!realMode)
				Profiler.EndSample();

			return calculTime;
		}

		public float Process(BaseGraph graph)
		{
			float	calculTime = 0f;
			bool	realMode = graph.IsRealMode();
			
			currentGraph = graph;

			hasProcessed = false;
		
			if (graph.GetComputeSortedNodes() == null)
				graph.UpdateComputeOrder();
			
			try {
				foreach (var node in graph.GetComputeSortedNodes())
				{
					//ignore unlinked nodes
					if (node.computeOrder < 0)
						continue ;
					
					calculTime += ProcessNode(node, realMode);
				}
			} catch (Exception e) {
				Debug.LogError(e);
				return calculTime;
			}
			
			hasProcessed = true;

			return calculTime;
		}

		public float ProcessNodes(BaseGraph graph, List< BaseNode > nodes)
		{
			float	calculTime = 0f;
			bool	realMode = graph.IsRealMode();
			
			currentGraph = graph;

			//sort nodes by compute order:
			nodes.Sort((n1, n2) => n1.computeOrder.CompareTo(n2.computeOrder));

			foreach(var node in nodes)
			{
				if (node.computeOrder < 0)
					continue ;
				
				calculTime += ProcessNode(node, realMode);
			}

			return calculTime;
		}
		
		public void	ProcessOnce(BaseGraph graph)
		{
			if (graph.GetComputeSortedNodes() == null)
				graph.UpdateComputeOrder();

			currentGraph = graph;

			foreach (var node in graph.GetComputeSortedNodes())
			{
				//ignore unlinked nodes
				if (node.computeOrder < 0)
					continue ;
				
				node.OnNodeProcessOnce();

				ProcessNodeLinks(node, graph.IsRealMode());
			}
		}
	
		#endregion

		#region Utils
	
		public void UpdateNodeDictionary(Dictionary< int, BaseNode > nd)
		{
			nodesDictionary = nd;
		}

		#endregion
	}
}
