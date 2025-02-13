using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

public class GaussianSplatVariations : MonoBehaviour
{

    GaussianSplatRenderer[] gaussianSplatRenderers;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        gaussianSplatRenderers= GetComponentsInChildren<GaussianSplatRenderer>();
    }

    // Update is called once per frame
    void Update()
    {
        for (int i = 0; i< gaussianSplatRenderers.Length; i++)
        {
            gaussianSplatRenderers[i].m_OpacityScale = Mathf.PingPong(Time.time / (1f*(float)(i+1)), 1.0f);
            gaussianSplatRenderers[i].m_SplatScale = Mathf.PingPong(Time.time / (1f* (float)(i+1)), 1f); 
            gaussianSplatRenderers[i].m_Saturation = Mathf.PingPong(Time.time / (1f * (float)(i + 1)), 10f); 
        }

        float t0 = Mathf.PingPong(Time.time * 0.5f, 1.0f);
        gaussianSplatRenderers[0].m_OverColor = Color.Lerp(Color.white, Color.blue, t0);
        float t1 = Mathf.PingPong(Time.time * 0.7f, 1.0f);
        gaussianSplatRenderers[1].m_OverColor = Color.Lerp(Color.white, Color.yellow, t1);
        float t2 = Mathf.PingPong(Time.time * 0.3f, 1.0f);
        gaussianSplatRenderers[2].m_OverColor = Color.Lerp(Color.white, Color.red, t2);
        float t3 = Mathf.PingPong(Time.time * 0.4f, 1.0f);
        gaussianSplatRenderers[3].m_OverColor = Color.Lerp(Color.white, Color.grey, t2);
    }
}
