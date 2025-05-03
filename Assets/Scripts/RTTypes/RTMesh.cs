using UnityEngine;

public class RTMesh : MonoBehaviour
{
    public RTMaterial material; 
    public bool flipNormals = false;

    [HideInInspector] public bool hasInitialized;
    
    void OnValidate() {
        if (!hasInitialized) {
            hasInitialized = true;
            material.Initialize();
        }
    }
}
