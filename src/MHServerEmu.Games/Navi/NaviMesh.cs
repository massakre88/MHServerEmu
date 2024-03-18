﻿using MHServerEmu.Core.Collisions;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Navi
{
    public class NaviMesh
    {
        public struct NaviMeshConnection
        {
            public NaviMesh Mesh;
            public NaviEdge Edge;
        }

        public struct ModifyMeshPatch
        {
            public Transform3 Transform;
            public NaviPatchPrototype Patch;
        }

        private readonly NaviSystem _navi;

        public Aabb Bounds { get; private set; }
        public NaviVertexLookupCache NaviVertexLookupCache { get; private set; }
        public NaviCdt NaviCdt { get; private set; }

        private bool _isInit;
        private float _padding;
        private Region _region;
        private NaviPoint[] _points;
        private List<NaviEdge> _edges;
        public Dictionary<NaviEdge, NaviMeshConnection> MeshConnections { get; private set; }
        private NaviEdge _exteriorSeedEdge;
        private List<ModifyMeshPatch> _modifyMeshPatches;
        private List<ModifyMeshPatch> _modifyMeshPatchesProjZ;

        public NaviMesh(NaviSystem navi)
        {
            _navi = navi;
            Bounds = Aabb.Zero;
            NaviVertexLookupCache = new(navi);
            NaviCdt = new(navi, NaviVertexLookupCache);
            _edges = new();
            MeshConnections = new(); 
            _modifyMeshPatches = new();
            _modifyMeshPatchesProjZ = new();
        }

        public void Initialize(Aabb bounds, float padding, Region region)
        {
            Release();
            Bounds = bounds;
            _padding = padding;
            _region = region;

            if (Bounds.IsValid() == false) return;

            var globals = GameDatabase.GlobalsPrototype;
            if (globals == null) return;

            float naviBudgetArea = globals.NaviBudgetBaseCellSizeWidth * globals.NaviBudgetBaseCellSizeLength;
            float naviBudgetPoints = globals.NaviBudgetBaseCellMaxPoints / naviBudgetArea;

            float naviMeshArea = Bounds.Width * Bounds.Length;
            var maxMeshPoints = (int)(naviBudgetPoints * naviMeshArea);

            NaviVertexLookupCache.Initialize(maxMeshPoints);
            NaviCdt.Create(Bounds.Expand(padding));
            AddSuperQuad(Bounds, padding);
        }

        private void AddSuperQuad(Aabb bounds, float padding)
        {
            float xMin = bounds.Min.X - padding;
            float xMax = bounds.Max.X + padding;
            float yMin = bounds.Min.Y - padding;
            float yMax = bounds.Max.Y + padding;

            NaviPoint p0 = new(new (xMin, yMin, 0.0f));
            NaviPoint p1 = new(new (xMax, yMin, 0.0f));
            NaviPoint p2 = new(new (xMax, yMax, 0.0f));
            NaviPoint p3 = new(new (xMin, yMax, 0.0f));

            NaviEdge e0 = new(p0, p1, NaviEdgeFlags.Flag0, new());
            NaviEdge e1 = new(p1, p2, NaviEdgeFlags.Flag0, new());
            NaviEdge e2 = new(p2, p3, NaviEdgeFlags.Flag0, new());
            NaviEdge e3 = new(p3, p0, NaviEdgeFlags.Flag0, new());

            NaviEdge e02 = new(p0, p2, NaviEdgeFlags.None, new());

            NaviCdt.AddTriangle(new(e0, e1, e02));
            NaviCdt.AddTriangle(new(e2, e3, e02));

            _exteriorSeedEdge = e0;
        }

        public void Release()
        {
            _isInit = false;
            _modifyMeshPatches.Clear();
            _modifyMeshPatchesProjZ.Clear();
            DestroyMeshConnections();
            _edges.Clear();
            _exteriorSeedEdge = null;
            ClearGenerationCache();
            NaviCdt.Release();
        }

        private void ClearGenerationCache()
        {
            NaviVertexLookupCache.Clear();
            _points = null;
        }

        private void DestroyMeshConnections()
        {
            foreach (var connection in MeshConnections)
            {
                NaviMeshConnection meshConnection = connection.Value;
                meshConnection.Mesh.MeshConnections.Remove(meshConnection.Edge);
            }
            MeshConnections.Clear();
        }

        public bool GenerateMesh()
        {
            _isInit = false;

            if (_modifyMeshPatches.Any())
            {
                foreach (var patch in _modifyMeshPatches)
                    if (ModifyMesh(patch.Transform, patch.Patch, false) == false) break;
                if (_navi.CheckErrorLog(false)) return false;
            }
            _modifyMeshPatches.Clear();

            if (_modifyMeshPatchesProjZ.Any())
            {
                foreach (var patch in _modifyMeshPatchesProjZ)
                    if (ModifyMesh(patch.Transform, patch.Patch, true) == false) break;
                if (_navi.CheckErrorLog(false)) return false;
            }
            _modifyMeshPatchesProjZ.Clear();

            MarkupMesh(false);
            if (_navi.CheckErrorLog(false)) return false;

            bool removeCollinearEdges = true; 
            if (removeCollinearEdges)
            {
                NaviCdt.RemoveCollinearEdges();
                if (_navi.CheckErrorLog(false)) return false;

                MarkupMesh(false);
                if (_navi.CheckErrorLog(false)) return false;
            }

            MergeMeshConnections();
            if (_navi.CheckErrorLog(false)) return false;

            _isInit = true;
            return true;
        }

        private void MergeMeshConnections() {}

        public bool ModifyMesh(Transform3 transform, NaviPatchPrototype patch, bool projZ)
        {
            if (patch.Points.IsNullOrEmpty()) return true;

            _points = new NaviPoint[patch.Points.Length];
            foreach (var edge in patch.Edges)
            {
                NaviPoint p0 = _points[edge.Index0];
                if (p0 == null)
                {
                    Vector3 Pos0 = new (transform * new Point3(patch.Points[edge.Index0]));
                    p0 = projZ ? NaviCdt.AddPointProjZ(Pos0) : NaviCdt.AddPoint(Pos0);
                    _points[edge.Index0] = p0;
                }

                NaviPoint p1 = _points[edge.Index1];
                if (p1 == null)
                {
                    Vector3 Pos1 = new (transform * new Point3(patch.Points[edge.Index1]));
                    p1 = projZ ? NaviCdt.AddPointProjZ(Pos1) : NaviCdt.AddPoint(Pos1);
                    _points[edge.Index1] = p1;
                }

                if (_navi.HasErrors() && _navi.CheckErrorLog(false, patch.ToString())) return false;
                if (p0 == p1) continue;

                NaviCdt.AddEdge(new(p0, p1, NaviEdgeFlags.Flag0, new(edge.Flags0, edge.Flags1)));
            }

            if (_navi.HasErrors() && _navi.CheckErrorLog(false, patch.ToString())) return false;

            return true;
        }

        private void MarkupMesh(bool v)
        {
            throw new NotImplementedException();
        }

        public bool Stitch(NaviPatchPrototype patch, Transform3 transform)
        {
            if (patch.Points.HasValue())
            {
                ModifyMeshPatch modifyMeshPatch = new()
                {
                    Transform = transform,
                    Patch = patch
                };
                _modifyMeshPatches.Add(modifyMeshPatch);
            }
            return true;
        }

        public bool StitchProjZ(NaviPatchPrototype patch, Transform3 transform)
        {
            if (patch.Points.HasValue())
            {
                ModifyMeshPatch modifyMeshPatch = new()
                {
                    Transform = transform,
                    Patch = patch
                };
                _modifyMeshPatchesProjZ.Add(modifyMeshPatch);
            }
            return true;
        }
    }
}
