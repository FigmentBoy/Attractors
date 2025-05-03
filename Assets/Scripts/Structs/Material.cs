using Unity.VisualScripting;
using UnityEngine;

[System.Serializable]
public struct RTMaterial
{
    public Color albedo;
    
    public Color specularColor;
    [Range(0, 1)] public float specularProbability;

    public Color emissionColor;

    [Min(0)] public float emissiveStrength;

    [Range(0, 1)] public float roughness;


    public void Initialize()
    {
        albedo = Color.white;
        emissionColor = Color.white;
        specularColor = Color.white;
        specularProbability = 0.0f;
        emissiveStrength = 0.0f;
        roughness = 1.0f;
    }
}
