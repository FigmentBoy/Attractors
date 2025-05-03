using UnityEngine;

[System.Serializable]
public struct Triangle
{
    public int a, b, c;
    public int isDoubleSided; // Usually just set to true, unless normals are flipped
    public RTMaterial material; 
}
