using UnityEngine;

[System.Serializable]
public struct BVHNode
{
    public Vector3 min;
    public Vector3 max;
    public int leftChild; // -1 if no child
    public int rightChild; // -1 if no child
    public int triangleIndex; // -1 if this is an internal node
    public int triangleCount; // 0 if this is an internal node
}