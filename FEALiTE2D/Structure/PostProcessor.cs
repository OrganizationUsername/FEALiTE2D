﻿using FEALiTE2D.Elements;
using FEALiTE2D.Loads;
using System;
using System.Collections.Generic;
using System.Linq;
using FEALiTE2D.Meshing;

namespace FEALiTE2D.Structure;

/// <summary>
/// This class represents a post-processor for <see cref="Structure"/> class to retrieve elements data after the structure is solved. 
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
public class PostProcessor
{
    private readonly Structure _structure;

    /// <summary>
    /// Creates a new instance of <see cref="PostProcessor"/> class.
    /// </summary>
    /// <param name="structure">a structure to extract data from.</param>
    public PostProcessor(Structure structure)
    {
        if (structure == null)
        {
            throw new ArgumentNullException(nameof(structure));
        }
        if (structure.AnalysisStatus == AnalysisStatus.Failure)
        {
            throw new ArgumentException("Structure should be successfully run.");
        }
        _structure = structure;
    }

    /// <summary>
    /// Get support reaction for a load case.
    /// </summary>
    /// <param name="node">node which is restrained</param>
    /// <param name="loadCase">a load case</param>
    /// <returns>support reaction</returns>
    public Force GetSupportReaction(Node2D node, LoadCase loadCase)
    {
        var r = new Force();

        if (node.IsFree) { return null; }

        // check if this node have an elastic support
        if (node.Support is NodalSpringSupport ns)
        {
            // F= K * D
            // get node displacement
            var d = GetNodeGlobalDisplacement(node, loadCase);
            var f = new double[3];
            ns.GlobalStiffnessMatrix.Multiply(d.ToVector(), f);

            r.Fx -= f[0];
            r.Fy -= f[1];
            r.Mz -= f[2];

            return r;
        }

        //add external loads on nodes
        foreach (var load in node.NodalLoads.Where(ii => ii.LoadCase == loadCase))
        {
            r.Fx -= load.Fx;
            r.Fy -= load.Fy;
            r.Mz -= load.Mz;
        }

        // get connected elements to this node
        var connectedElements = _structure.Elements.Where(o => o.Nodes.Contains(node));

        // reactions are the sum of global external fixed end forces
        foreach (var e in connectedElements)
        {
            var fixedEndForces = GetElementGlobalFixedEndForces(e, loadCase);

            if (e.Nodes[0] == node)
            {
                r.Fx += fixedEndForces[0];
                r.Fy += fixedEndForces[1];
                r.Mz += fixedEndForces[2];
            }
            else
            {
                r.Fx += fixedEndForces[3];
                r.Fy += fixedEndForces[4];
                r.Mz += fixedEndForces[5];
            }
        }

        // get rid of redundant values which are very small such as 1e-15
        if (node.IsRestrained(NodalDegreeOfFreedom.Ux) != true) r.Fx = 0;
        if (node.IsRestrained(NodalDegreeOfFreedom.Uy) != true) r.Fy = 0;
        if (node.IsRestrained(NodalDegreeOfFreedom.Rz) != true) r.Mz = 0;

        return r;
    }

    /// <summary>
    /// Get support reaction for a load combination of load cases.
    /// </summary>
    /// <param name="node">node which is restrained</param>
    /// <param name="loadCombination">a load combination</param>
    /// <returns>support reaction</returns>
    public Force GetSupportReaction(Node2D node, LoadCombination loadCombination)
    {
        var r = new Force();
        foreach (var lc in loadCombination)
        {
            r += lc.Value * GetSupportReaction(node, lc.Key);
        }
        return r;
    }

    /// <summary>
    /// Get Node's global displacement due to applied load in a load case.
    /// </summary>
    /// <param name="node">node which is has a free dof.</param>
    /// <param name="loadCase">load case</param>
    /// <returns>Nodal Displacement</returns>
    public Displacement GetNodeGlobalDisplacement(Node2D node, LoadCase loadCase)
    {
        var nd = new Displacement();

        if (_structure.AnalysisStatus != AnalysisStatus.Successful) return nd;
        foreach (var load in node.SupportDisplacementLoad)
        {
            if (load.LoadCase != loadCase) continue;
            nd.Ux += load.Ux;
            nd.Uy += load.Uy;
            nd.Rz += load.Rz;
        }

        // get displacement from displacement vector if this node is free
        var dVector = _structure.DisplacementVectors[loadCase];
        if (node.DegreeOfFreedomIndices[0] < dVector.Length) { nd.Ux = dVector[node.DegreeOfFreedomIndices[0]]; }
        if (node.DegreeOfFreedomIndices[1] < dVector.Length) { nd.Uy = dVector[node.DegreeOfFreedomIndices[1]]; }
        if (node.DegreeOfFreedomIndices[2] < dVector.Length) { nd.Rz = dVector[node.DegreeOfFreedomIndices[2]]; }
        return nd;
    }

    /// <summary>
    /// Get Local End forces of an element after the structure is solved.
    /// </summary>
    /// <param name="element">an element</param>
    /// <param name="loadCase">a load case</param>
    /// <returns>Local End forces of an element after the structure is solved.</returns>
    public double[] GetElementLocalFixedEndForces(IElement element, LoadCase loadCase)
    {
        var results = new double[6];

        // Q = k*u+qf
        var dl = GetElementLocalEndDisplacement(element, loadCase);

        //ql = kl*dl
        element.LocalStiffnessMatrix.Multiply(dl, results);

        // get external fixed end loads from element load dictionary.
        var globalEndForcesForLoadCase = element.GlobalEndForcesForLoadCase[loadCase];
        var globalResults = new double[globalEndForcesForLoadCase.Length];
        element.TransformationMatrix.Multiply(globalEndForcesForLoadCase, globalResults);

        for (var i = 0; i < globalResults.Length; i++)
        {
            results[i] += globalResults[i];
        }

        return results;
    }

    /// <summary>
    /// Get Global End forces of an element after the structure is solved.
    /// </summary>
    /// <param name="element">an element</param>
    /// <param name="loadCase">a load case</param>
    /// <returns>Global End forces of an element after the structure is solved.</returns>
    public double[] GetElementGlobalFixedEndForces(IElement element, LoadCase loadCase)
    {
        var ql = GetElementLocalFixedEndForces(element, loadCase);

        // fg = Tt*fl
        var fg = new double[ql.Length];
        element.TransformationMatrix.TransposeMultiply(ql, fg);
        return fg;
    }

    /// <summary>
    /// Get a local end displacement of an element at a given load case.
    /// </summary>
    /// <param name="element">an element to process</param>
    /// <param name="loadCase">a load case to display displacement</param>
    public double[] GetElementLocalEndDisplacement(IElement element, LoadCase loadCase)
    {
        // get displacement of fist node and second node of the element
        var nd1 = GetNodeGlobalDisplacement(element.Nodes[0], loadCase);
        var nd2 = GetNodeGlobalDisplacement(element.Nodes[1], loadCase);

        var dg = new[] { nd1.Ux, nd1.Uy, nd1.Rz, nd2.Ux, nd2.Uy, nd2.Rz };
        var dl = new double[dg.Length];
        element.TransformationMatrix.Multiply(dg, dl); // dl = T*dg

        return dl;
    }

    /// <summary>
    /// Get internal forces and local displacements of an element. note that segments length and count are based on the <see cref="FEALiTE2D.Meshing.ILinearMesher"/>
    /// </summary>
    /// <param name="element">an element to get its internal forces</param>
    /// <param name="loadCase">a load case to get the internal forces in an element</param>
    /// <returns>List of segments containing internal forces and displacements of an element.</returns>
    public List<LinearMeshSegment> GetElementInternalForces(IElement element, LoadCase loadCase)
    {
        var len = element.Length;

        // get local end forces after the structure is solved
        var fl = GetElementLocalFixedEndForces(element, loadCase);
        var dl = GetElementLocalEndDisplacement(element, loadCase);

        //loop through each segment
        for (var i = 0; i < element.MeshSegments.Count; i++)
        {
            var segment = element.MeshSegments[i];
            var x = segment.x1;

            // force 0 values for start and end uniform and Trap. loads.
            segment.wx1 = 0; segment.wx2 = 0;
            segment.wy1 = 0; segment.wy2 = 0;

            //assign local end displacements and rotations to start segment
            if (i == 0)
            {
                segment.Displacement1 = new Displacement()
                {
                    Ux = dl[0],
                    Uy = dl[1],
                    Rz = dl[2]
                };
            }
            else
            {
                // get a reference to previous segment
                var pS = element.MeshSegments[i - 1];
                segment.Displacement1.Ux = pS.AxialDisplacementAt(pS.x2 - pS.x1);
                segment.Displacement1.Uy = pS.VerticalDisplacementAt(pS.x2 - pS.x1);
                segment.Displacement1.Rz = pS.SlopeAngleAt(pS.x2 - pS.x1);

                // use shape function for elements with end releases.
                if (element is FrameElement2D ee)
                {
                    if (ee.EndRelease != Frame2DEndRelease.NoRelease)
                    {
                        // get shape function at start of the segment
                        var n = ee.GetShapeFunctionAt(segment.x1);
                        var u = new double[3];
                        n.Multiply(dl, u);
                        segment.Displacement1 = Displacement.FromVector(u);
                    }
                }

            }

            // assign local end forces at start of each segment 
            segment.InternalForces1.Fx = fl[0];
            segment.InternalForces1.Fy = fl[1];
            segment.InternalForces1.Mz = fl[2] - fl[1] * x;

            // add effect of point load, uniform and trap. load to start of each segment if this load is before the segment
            var loads = element.Loads.Where(o => o.LoadCase == loadCase);
            foreach (var load in loads)
            {
                if (load is FramePointLoad pL)
                {
                    if (!(pL.L1 <= x)) continue;
                    // get point load in lcs.
                    var p = pL.GetLoadValueAt(element, pL.L1) as FramePointLoad;
                    segment.InternalForces1.Fx += p.Fx;
                    segment.InternalForces1.Fy += p.Fy;
                    segment.InternalForces1.Mz += p.Mz - p.Fy * (x - pL.L1);
                }
                else if (load is FrameUniformLoad uL)
                {
                    // check the load is before or just before the segment
                    if (!(uL.L1 <= x)) continue;
                    // get uniform load in lcs.
                    var uniformLoad = uL.GetLoadValueAt(element, x) as FrameUniformLoad;
                    double wx = uniformLoad.Wx,
                        wy = uniformLoad.Wy,
                        x1 = uL.L1,
                        x2 = len - uL.L2; // make x2 measured from start of the element

                    // check if the load stops at start  of the segment 
                    if (x2 > x)
                    {
                        segment.wx1 += wx;
                        segment.wx2 += wx;
                        segment.wy1 += wy;
                        segment.wy2 += wy;
                        x2 = x;
                    }

                    segment.InternalForces1.Fx += wx * (x2 - x1);
                    segment.InternalForces1.Fy += wy * (x2 - x1);
                    segment.InternalForces1.Mz -= wy * (x2 - x1) * (x - 0.5 * x2 - 0.5 * x1);
                }
                else if (load is FrameTrapezoidalLoad tL)
                {
                    // check the load is before or just before the segment
                    if (!(tL.L1 <= x)) continue;
                    // get trap load in lcs.
                    var tl1 = tL.GetLoadValueAt(element, tL.L1) as FrameTrapezoidalLoad;
                    var tl2 = tL.GetLoadValueAt(element, len - tL.L2) as FrameTrapezoidalLoad;
                    double wx1 = tl1.Wx1,
                        wx2 = tl2.Wx1,
                        wy1 = tl1.Wy1,
                        wy2 = tl2.Wy1,
                        x1 = tl1.L1,
                        x2 = len - tL.L2; // make x2 of the load measured from start of the element

                    //check if the load stops at start  of the segment
                    if (x2 > x)
                    {
                        // add load to the segment.
                        segment.wx1 += ((wx2 - wx1) / (x2 - x1)) * (x - x1) + wx1;
                        segment.wx2 += ((wx2 - wx1) / (x2 - x1)) * (segment.x2 - x1) + wx1;
                        segment.wy1 += ((wy2 - wy1) / (x2 - x1)) * (x - x1) + wy1;
                        segment.wy2 += ((wy2 - wy1) / (x2 - x1)) * (segment.x2 - x1) + wy1;

                        //measure the load at start of the segment if it passes the start of the segment
                        wx2 = ((wx2 - wx1) / (x2 - x1)) * (x - x1) + wx1;
                        wy2 = ((wy2 - wy1) / (x2 - x1)) * (x - x1) + wy1;
                        x2 = x;
                    }

                    segment.InternalForces1.Fx += 0.5 * (wx1 + wx2) * (x2 - x1);
                    segment.InternalForces1.Fy += 0.5 * (wy1 + wy2) * (x2 - x1);
                    segment.InternalForces1.Mz += (2 * wy1 * x1 - 3 * wy1 * x + wy1 * x2 + wy2 * x1 - 3 * wy2 * x + 2 * wy2 * x2) * (x2 - x1) / 6;
                }
            }

            // set internal forces at the end
            segment.InternalForces2 = segment.GetInternalForceAt(segment.x2 - segment.x1);
            segment.Displacement2 = segment.GetDisplacementAt(segment.x2 - segment.x1);

            // use shape function for elements with end releases.
            if (element is not FrameElement2D eee) continue;
            {
                if (eee.EndRelease == Frame2DEndRelease.NoRelease) continue;
                // get shape function at start of the segment
                var n = eee.GetShapeFunctionAt(segment.x2);
                var u = new double[3];
                n.Multiply(dl, u);
                segment.Displacement2 = Displacement.FromVector(u);
            }
        }

        return element.MeshSegments;
    }

    /// <summary>
    /// Get internal forces and local displacements of an element. note that segments length and count are based on the <see cref="FEALiTE2D.Meshing.ILinearMesher"/>
    /// </summary>
    /// <param name="element">an element to get its internal forces</param>
    /// <param name="loadCombination">a load combination</param>
    /// <returns>List of segments containing internal forces and displacements of an element.</returns>
    public List<LinearMeshSegment> GetElementInternalForces(IElement element, LoadCombination loadCombination)
    {
        var results = new List<LinearMeshSegment>();
        foreach (var meshSegment in element.MeshSegments)
        {
            results.Add(new()
            {
                x1 = meshSegment.x1,
                x2 = meshSegment.x2,
                A = meshSegment.A,
                Ix = meshSegment.Ix,
                E = meshSegment.E
            });
        }

        foreach (var lc in loadCombination)
        {
            var segments = GetElementInternalForces(element, lc.Key);

            for (var i = 0; i < segments.Count; i++)
            {
                // get reference to current segments
                var cSegment = segments[i];
                var cListItem = results[i];
                cListItem.fx += lc.Value * cSegment.fx;
                cListItem.fy += lc.Value * cSegment.fy;
                cListItem.mz += lc.Value * cSegment.mz;
                cListItem.wx1 += lc.Value * cSegment.wx1;
                cListItem.wx2 += lc.Value * cSegment.wx2;
                cListItem.wy1 += lc.Value * cSegment.wy1;
                cListItem.wy2 += lc.Value * cSegment.wy2;
                cListItem.Displacement1 += lc.Value * cSegment.Displacement1;
                cListItem.Displacement2 += lc.Value * cSegment.Displacement2;
                cListItem.InternalForces1 += lc.Value * cSegment.InternalForces1;
                cListItem.InternalForces2 += lc.Value * cSegment.InternalForces2;
            }
        }
        return results;
    }

    /// <summary>
    /// Get internal forces of an element at a distance measured from start node.
    /// </summary>
    /// <param name="element">an element to get its internal forces</param>
    /// <param name="loadCase">a load case to get the internal forces in an element</param>
    /// <param name="x">distance measured from start node.</param>
    /// <returns>internal forces of an element at a distance.</returns>
    public Force GetElementInternalForcesAt(IElement element, LoadCase loadCase, double x)
    {
        // get a list of meshed segments of the element
        var segments = GetElementInternalForces(element, loadCase);

        // loop through the segments to find which segment this distance is bounded to.
        foreach (var segment in segments)
        {
            if (x >= segment.x1 && x <= segment.x2)
            {
                return segment.GetInternalForceAt(x - segment.x1);
            }
        }
        return null;
    }

    /// <summary>
    /// Get internal forces of an element at a distance measured from start node.
    /// </summary>
    /// <param name="element">an element to get its internal forces</param>
    /// <param name="loadCombination">a load combination of load case to get the internal forces in an element</param>
    /// <param name="x">distance measured from start node.</param>
    /// <returns>internal forces of an element at a distance.</returns>
    public Force GetElementInternalForcesAt(IElement element, LoadCombination loadCombination, double x)
    {
        var segments = GetElementInternalForces(element, loadCombination);

        // loop through the segments to find which segment this distance is bounded to.
        foreach (var segment in segments)
        {
            if (x >= segment.x1 && x <= segment.x2)
            {
                return segment.GetInternalForceAt(x - segment.x1);
            }
        }
        return null;
    }

    /// <summary>
    /// Get displacement of an element at a distance measured from start node.
    /// </summary>
    /// <param name="element">an element to get its displacement</param>
    /// <param name="loadCase">a load case to get the displacement in an element</param>
    /// <param name="x">distance measured from start node.</param>
    /// <returns>displacement of an element at a distance.</returns>
    public Displacement GetElementDisplacementAt(IElement element, LoadCase loadCase, double x)
    {
        // get a list of meshed segments of the element
        var segments = GetElementInternalForces(element, loadCase);

        // loop through the segments to find which segment this distance is bounded to.
        foreach (var segment in segments)
        {
            if (x >= segment.x1 && x <= segment.x2)
            {
                return segment.GetDisplacementAt(x - segment.x1);
            }
        }
        return null;
    }

    /// <summary>
    /// Get displacement of an element at a distance measured from start node.
    /// </summary>
    /// <param name="element">an element to get its displacement</param>
    /// <param name="loadCombination">a load combination of load case to get the internal forces in an element</param>
    /// <param name="x">distance measured from start node.</param>
    /// <returns>displacement of an element at a distance.</returns>
    public Displacement GetElementDisplacementAt(IElement element, LoadCombination loadCombination, double x)
    {
        // get a list of meshed segments of the element
        var segments = GetElementInternalForces(element, loadCombination);

        // loop through the segments to find which segment this distance is bounded to.
        foreach (var segment in segments)
        {
            if (x >= segment.x1 && x <= segment.x2)
            {
                return segment.GetDisplacementAt(x - segment.x1);
            }
        }
        return null;
    }

}