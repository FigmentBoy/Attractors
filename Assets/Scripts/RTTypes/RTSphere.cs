using UnityEngine;

public class RTSphere : MonoBehaviour
{
    public RTMaterial material;
    
    [HideInInspector] public bool hasInitialized;
    
    void OnValidate() {
        if (!hasInitialized) {
            hasInitialized = true;
            material.Initialize();
        }
    }
}
