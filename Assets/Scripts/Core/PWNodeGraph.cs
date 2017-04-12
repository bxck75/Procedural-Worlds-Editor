﻿using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using System.Linq;
using System;

namespace PW
{
	public enum	PWOutputType
	{
		NONE,
		SIDEVIEW_2D,
		TOPDOWNVIEW_2D,
		PLANE_3D,
		SPHERICAL_3D,
		CUBIC_3D,
		DENSITY_1D,
		DENSITY_2D,
		DENSITY_3D,
		MESH,
	}

	[CreateAssetMenu(fileName = "New ProceduralWorld", menuName = "Procedural World", order = 1)]
	[System.SerializableAttribute]
	public class PWNodeGraph : ScriptableObject {
	
		[SerializeField]
		public List< PWNode >				nodes = new List< PWNode >();
		
		[SerializeField]
		public HorizontalSplitView			h1;
		[SerializeField]
		public HorizontalSplitView			h2;
	
		[SerializeField]
		public Vector2						leftBarScrollPosition;
		[SerializeField]
		public Vector2						selectorScrollPosition;
	
		[SerializeField]
		public string						externalName;
		[SerializeField]
		public string						assetName;
		[SerializeField]
		public string						assetPath;
		[SerializeField]
		public string						saveName;
		[SerializeField]
		public Vector2						graphDecalPosition;
		[SerializeField]
		[HideInInspector]
		public int							localWindowIdCount;
		[SerializeField]
		[HideInInspector]
		public string						firstInitialization = null;
		[SerializeField]
		public bool							realMode;
		
		[SerializeField]
		[HideInInspector]
		public string						searchString = "";

		[SerializeField]
		public bool							presetChoosed;

		[SerializeField]
		public int							chunkSize;
		[SerializeField]
		public int							seed;

		[SerializeField]
		public PWOutputType					outputType;

		[SerializeField]
		public List< string >				subgraphReferences = new List< string >();
		[SerializeField]
		public string						parentReference;

		[SerializeField]
		public PWNode						inputNode;
		[SerializeField]
		public PWNode						outputNode;
		[SerializeField]
		public PWNode						externalGraphNode;

		[System.NonSerializedAttribute]
		IOrderedEnumerable< PWNodeComputeInfo > computeOrderSortedNodes = null;

		[System.NonSerializedAttribute]
		private bool						graphInstanciesLoaded = false;
		[System.NonSerializedAttribute]
		public bool							unserializeInitialized = false;

		[System.NonSerializedAttribute]
		public Dictionary< string, PWNodeGraph > graphInstancies = new Dictionary< string, PWNodeGraph >();
		[System.NonSerializedAttribute]
		public Dictionary< int, PWNode >	nodesDictionary = new Dictionary< int, PWNode >();

		[System.NonSerializedAttribute]
		Dictionary< string, Dictionary< string, FieldInfo > > bakedNodeFields = new Dictionary< string, Dictionary< string, FieldInfo > >();

		[System.NonSerializedAttribute]
		List< Type > allNodeTypeList = new List< Type > {
			typeof(PWNodeSlider),
			typeof(PWNodeAdd),
			typeof(PWNodeDebugLog),
			typeof(PWNodeCircleNoiseMask),
			typeof(PWNodePerlinNoise2D),
			typeof(PWNodeSideView2DTerrain), typeof(PWNodeTopDown2DTerrain),
			typeof(PWNodeGraphInput), typeof(PWNodeGraphOutput), typeof(PWNodeGraphExternal),
		};

		void BakeNode(Type t)
		{
			var dico = new Dictionary< string, FieldInfo >();
			bakedNodeFields[t.AssemblyQualifiedName] = dico;
	
			foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
				dico[field.Name] = field;
		}
	
		public void OnEnable()
		{
			//bake node fields to accelerate data transfer from node to node.
			bakedNodeFields.Clear();
			foreach (var nodeType in allNodeTypeList)
				BakeNode(nodeType);

			LoadGraphInstances();
			
			//add all existing nodes to the nodesDictionary
			foreach (var node in nodes)
				nodesDictionary[node.windowId] = node;
			foreach (var subgraphName in subgraphReferences)
			{
				var subgraph = FindGraphByName(subgraphName);

				if (subgraph != null && subgraph.externalGraphNode != null)
					nodesDictionary[subgraph.externalGraphNode.windowId] = subgraph.externalGraphNode;
			}
			if (externalGraphNode != null)
				nodesDictionary[externalGraphNode.windowId] = externalGraphNode;
			if (inputNode != null)
				nodesDictionary[inputNode.windowId] = inputNode;
			if (outputNode != null)
				nodesDictionary[outputNode.windowId] = outputNode;
		}

		private class PWNodeComputeInfo
		{
			public PWNode node;
			public string graphName;

			public PWNodeComputeInfo(PWNode n, string g) {
				node = n;
				graphName = g;
			}
		}

		public void	UpdateComputeOrder()
		{
			computeOrderSortedNodes = nodesDictionary
					//select all nodes building an object with node value and graph name (if needed)
					.Select(kp => new PWNodeComputeInfo(kp.Value,
						//if node is an external node, find the name of his graph
						(kp.Value.GetType() == typeof(PWNodeGraphExternal)
							? subgraphReferences.FirstOrDefault(gName => {
								var g = FindGraphByName(gName);
								if (g.externalGraphNode.windowId == kp.Value.windowId)
									return true;
								return false;
								})
					: null)))
					//sort the resulting list by computeOrder:
					.OrderBy(n => n.node.computeOrder);
		}

		void ProcessNodeLinks(PWNode node)
		{
			var links = node.GetLinks();
	
			foreach (var link in links)
			{
				if (!nodesDictionary.ContainsKey(link.distantWindowId))
					continue;

				var target = nodesDictionary[link.distantWindowId];
	
				if (target == null)
					continue ;
	
				var val = bakedNodeFields[link.localClassAQName][link.localName].GetValue(node);
				var prop = bakedNodeFields[link.distantClassAQName][link.distantName];
				Debug.Log("distant info: " + link.distantClassAQName + " | " + link.distantName + " | " + link.distantIndex);
				Debug.Log("local info: " + link.localClassAQName + " | "  + link.localName + " | " + link.localIndex);
				if (link.distantIndex == -1)
					prop.SetValue(target, val);
				else //multiple object data:
				{
					PWValues values = (PWValues)prop.GetValue(target);
	
					if (values != null)
					{
						if (!values.AssignAt(link.distantIndex, val, link.localName))
							Debug.Log("failed to set distant indexed field value: " + link.distantName);
					}
				}
			}
		}

		public void ProcessGraph()
		{
			//here nodes are sorted by compute-order
			//TODO: rework this to get a working in-depth node process call
			//AND integrate notifyDataChanged in this todo.

			if (computeOrderSortedNodes == null)
				UpdateComputeOrder();
			
			foreach (var nodeInfo in computeOrderSortedNodes)
			{
				if (nodeInfo.graphName != null)
				{
					PWNodeGraph g = FindGraphByName(nodeInfo.graphName);
					g.ProcessGraph();
				}
				else
				{
					nodeInfo.node.Process();
					ProcessNodeLinks(nodeInfo.node);
				}
			}
		}

		public void	UpdateSeed(int seed)
		{
			this.seed = seed;
			ForeachAllNodes((n) => n.seed = seed, true, true);
		}

		public void UpdateChunkPosition(Vector3 chunkPos)
		{
			ForeachAllNodes((n) => n.chunkPosition = chunkPos, true, true);
		}

		public void UpdateChunkSize(int chunkSize)
		{
			this.chunkSize = chunkSize;
			ForeachAllNodes((n) => n.chunkSize = chunkSize, true, true);
		}

		void LoadGraphInstances()
		{
			//load all available graph instancies in the AssetDatabase:
			if (!String.IsNullOrEmpty(assetPath))
			{
				int		resourceIndex = assetPath.IndexOf("Resources");
				if (resourceIndex != -1)
				{
					string resourcePath = Path.ChangeExtension(assetPath.Substring(resourceIndex + 10), null);
					var graphs = Resources.LoadAll(resourcePath, typeof(PWNodeGraph));
					foreach (var graph in graphs)
					{
						if (graphInstancies.ContainsKey(graph.name))
							continue ;
						Debug.Log("loaded graph: " + graph.name);
						graphInstancies.Add(graph.name, graph as PWNodeGraph);
					}
				}
			}
		}

		public PWNodeGraph FindGraphByName(string name)
		{
			PWNodeGraph		ret;
				
			if (name == null)
				return null;
			if (graphInstancies.TryGetValue(name, out ret))
				return ret;
			return null;
		}

		public void ForeachAllNodes(System.Action< PWNode > callback, bool recursive = false, bool graphInputAndOutput = false, PWNodeGraph graph = null)
		{
			if (graph == null)
				graph = this;
			foreach (var node in graph.nodes)
				callback(node);
			if (graphInputAndOutput)
			{
				callback(graph.inputNode);
				callback(graph.outputNode);
			}
			if (recursive)
				foreach (var subgraphName in graph.subgraphReferences)
				{
					var g = FindGraphByName(subgraphName);
					if (g != null)
						ForeachAllNodes(callback, recursive, graphInputAndOutput, g);
				}
		}
    }
}