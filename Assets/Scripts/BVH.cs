using System.Collections.Generic;
using UnityEngine;

public class BVH 
{
    public static BVHNode[] BuildBVH(Triangle[] triangles, Vertex[] vertices, int maxDepth = 10) 
    {
        if (triangles == null || vertices == null || triangles.Length == 0 || vertices.Length == 0) {
            return new BVHNode[0];
        }

        // Make a centroid for each triangle
        Vector3[] centroids = new Vector3[triangles.Length];
        for (int i = 0; i < triangles.Length; i++) {
            Vector3 v0 = vertices[triangles[i].a].position;
            Vector3 v1 = vertices[triangles[i].b].position;
            Vector3 v2 = vertices[triangles[i].c].position;
            centroids[i] = (v0 + v1 + v2) / 3.0f;
        }

        // Calculate the bounding box for all triangles
        Vector3 min = centroids[0];
        Vector3 max = centroids[0];

        for (int i = 1; i < centroids.Length; i++) {
            Vector3 v0 = vertices[triangles[i].a].position;
            Vector3 v1 = vertices[triangles[i].b].position;
            Vector3 v2 = vertices[triangles[i].c].position;
            min = Vector3.Min(min, v0);
            max = Vector3.Max(max, v0);
            min = Vector3.Min(min, v1);
            max = Vector3.Max(max, v1);
            min = Vector3.Min(min, v2);
            max = Vector3.Max(max, v2);
        }

        List<BVHNode> stack = new List<BVHNode>
        {
            new BVHNode {
                min = min,
                max = max,
                leftChild = -1,
                rightChild = -1,
                triangleIndex = 0,
                triangleCount = triangles.Length
            } // Start with the root node
        };

        Subdivide(ref stack, ref triangles, ref centroids, vertices, 0, maxDepth);

        BVHNode[] finalNodes = new BVHNode[stack.Count];
        for (int i = 0; i < stack.Count; i++) {
            finalNodes[i] = stack[i];
        }

        Debug.Log($"BVH built with {finalNodes.Length} nodes.");
        return finalNodes;
    }

    private static void Subdivide(ref List<BVHNode> stack, ref Triangle[] triangles, ref Vector3[] centroids, Vertex[] vertices, int index, int depth) 
    {
        if (stack.Count == 0) return;

        BVHNode currentNode = stack[index];
        int triangleCount = currentNode.triangleCount;
        
        int start = currentNode.triangleIndex;
        int end = start + triangleCount;

        if (triangleCount <= 2 || depth <= 0) {
            currentNode.leftChild = -1;
            currentNode.rightChild = -1;
            stack[index] = currentNode; // Update the current node in the stack
            return;
        }
        
        int bestAxis = -1;
        float bestPos = 0;
        float bestCost = float.MaxValue;
        int mid = (start + end) / 2;

        for (int axis = 0; axis < 3; axis++) {
            System.Array.Sort(centroids, triangles, start, triangleCount, Comparer<Vector3>.Create((a, b) => a[axis].CompareTo(b[axis])));
            
            for (int i = start; i < end; i++) {
                float pos = centroids[i][axis];
                float cost = EvaluateSAH(currentNode, centroids, axis, pos, start, end);
                
                if (cost < bestCost) {
                    bestCost = cost;
                    bestAxis = axis;
                    bestPos = pos;
                    mid = i;
                }
            }
        }

        if (bestAxis == -1) {
            currentNode.leftChild = -1;
            currentNode.rightChild = -1;
            stack[index] = currentNode; // Update the current node in the stack
            Debug.LogWarning("No valid axis found for subdivision, stopping further subdivision.");
            return;
        }

        // Sort triangles based on the centroid along the chosen axis
        System.Array.Sort(centroids, triangles, start, triangleCount, Comparer<Vector3>.Create((a, b) => a[bestAxis].CompareTo(b[bestAxis])));

        BVHNode leftChild = new BVHNode {
            min = centroids[start],
            max = centroids[start],
            leftChild = -1,
            rightChild = -1,
            triangleIndex = start,
            triangleCount = mid - start
        };

        BVHNode rightChild = new BVHNode {
            min = centroids[mid],
            max = centroids[mid],
            leftChild = -1,
            rightChild = -1,
            triangleIndex = mid,
            triangleCount = end - mid
        };

        // Calculate the bounding boxes for the left and right children
        for (int i = start; i < mid; i++) {
            Vector3 v0 = vertices[triangles[i].a].position;
            Vector3 v1 = vertices[triangles[i].b].position;
            Vector3 v2 = vertices[triangles[i].c].position;
            leftChild.min = Vector3.Min(leftChild.min, v0);
            leftChild.max = Vector3.Max(leftChild.max, v0);
            leftChild.min = Vector3.Min(leftChild.min, v1);
            leftChild.max = Vector3.Max(leftChild.max, v1);
            leftChild.min = Vector3.Min(leftChild.min, v2);
            leftChild.max = Vector3.Max(leftChild.max, v2);
        }

        for (int i = mid; i < end; i++) {
            Vector3 v0 = vertices[triangles[i].a].position;
            Vector3 v1 = vertices[triangles[i].b].position;
            Vector3 v2 = vertices[triangles[i].c].position;
            rightChild.min = Vector3.Min(rightChild.min, v0);
            rightChild.max = Vector3.Max(rightChild.max, v0);
            rightChild.min = Vector3.Min(rightChild.min, v1);
            rightChild.max = Vector3.Max(rightChild.max, v1);
            rightChild.min = Vector3.Min(rightChild.min, v2);
            rightChild.max = Vector3.Max(rightChild.max, v2);
        }

        // Assign the children to the current node
        currentNode.leftChild = stack.Count;
        currentNode.rightChild = stack.Count + 1;
        currentNode.triangleCount = 0; // This node is now an internal node
        currentNode.triangleIndex = -1; // No triangle index for internal nodes
        stack[index] = currentNode; // Update the current node in the stack

        stack.Add(leftChild);
        stack.Add(rightChild);

        // Recursively subdivide the left and right children
        Subdivide(ref stack, ref triangles, ref centroids, vertices, currentNode.leftChild, depth - 1);
        Subdivide(ref stack, ref triangles, ref centroids, vertices, currentNode.rightChild, depth - 1);
    }

    private static float EvaluateSAH(BVHNode node, Vector3[] centroids, int axis, float pos, int start, int end) 
    {
        Bounds leftBounds = new Bounds();
        Bounds rightBounds = new Bounds();
        int leftCount = 0;
        int rightCount = 0;

        for (int i = start; i < end; i++) 
        {
            if (centroids[i][axis] < pos) 
            {
                leftBounds.Encapsulate(centroids[i]);
                leftCount++;
            } 
            else 
            {
                rightBounds.Encapsulate(centroids[i]);
                rightCount++;
            }
        }

        float cost = leftCount * leftBounds.size.magnitude + rightCount * rightBounds.size.magnitude;
        return cost > 0 ? cost : float.MaxValue;
    }
}