using GaussianSplatting.Runtime;
using GaussianSplatting.RuntimeCreator;
using HSVPicker;
using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

public class GaussianSplatUI : MonoBehaviour
{
    public Slider PositionNoiseSlider;
    public Text PositionNoiseText;
    private GaussianSplatRenderer gsRenderer;
    private ColorPicker colorPicker;
    private GameObject panelGO;
    private GameObject openPanelGO;
    private float noiseAmount = 0;
    private float mem_noiseAmount = 0;
    private bool isComputing = false;

    private byte[] posDataBytes;
    NativeArray<byte> m_OutputNorm16;

    float3[] posFloatArray;
    NativeArray<byte> m_OutputFloat32;

    void Start()
    {
        gsRenderer = FindFirstObjectByType<GaussianSplatRenderer>();
        colorPicker = FindFirstObjectByType<ColorPicker>();
        colorPicker.gameObject.SetActive(false);
        panelGO = transform.GetChild(0).gameObject;
        openPanelGO = transform.GetChild(1).gameObject;
        openPanelGO.SetActive(false);

        // Norm16
        if (gsRenderer.HasValidAsset && gsRenderer.m_Asset.posFormat == GaussianSplatAsset.VectorFormat.Norm16)
        {
            posFloatArray = new float3[gsRenderer.m_Asset.splatCount];
            posDataBytes = gsRenderer.m_Asset.posData.bytes;

            for (int i = 0; i < gsRenderer.m_Asset.splatCount * 6; i += 6)
            {
                byte[] data = new byte[6];
                Array.Copy(posDataBytes, i, data, 0, 6);

                ulong result = 0;
                for (int k = 0; k < data.Length; k++)
                {
                    result |= (ulong)data[k] << (k * 8);
                }
                float3 value = GaussianSplatAssetRuntimeCreator.DecodeNorm16ToFloat3(result);
                posFloatArray[i / 6].x = value.x;
                posFloatArray[i / 6].y = value.y;
                posFloatArray[i / 6].z = value.z;
            }

            m_OutputNorm16 = new NativeArray<byte>(gsRenderer.m_Asset.splatCount * 6 + 2, Allocator.Persistent);
        }
        // Float32
        else if (gsRenderer.HasValidAsset && gsRenderer.m_Asset.posFormat == GaussianSplatAsset.VectorFormat.Float32)
        {
            posFloatArray = new float3[gsRenderer.m_Asset.splatCount];
            for (int i = 0; i < gsRenderer.m_Asset.splatCount * 3; i += 3)
            {
                posFloatArray[i / 3].x = gsRenderer.m_Asset.posData.GetData<float>()[i];
                posFloatArray[i / 3].y = gsRenderer.m_Asset.posData.GetData<float>()[i + 1];
                posFloatArray[i / 3].z = gsRenderer.m_Asset.posData.GetData<float>()[i + 2];
            }

            m_OutputFloat32 = new NativeArray<byte>(gsRenderer.m_Asset.splatCount * 12, Allocator.Persistent);
        }
        // Norm11 and Norm6
        else
        {
            PositionNoiseSlider.gameObject.SetActive(false);
            PositionNoiseText.gameObject.SetActive(false);
        }
    }

    private void Update()
    {
        if (noiseAmount != mem_noiseAmount && !isComputing)
        {
            SetPositionNoise();
            mem_noiseAmount = noiseAmount;
            PositionNoiseText.text = "Position noise : " + noiseAmount.ToString();
        }
    }

    public void SetGSOpacity(UnityEngine.UI.Slider slider)
    {
        gsRenderer.m_OpacityScale = slider.value;
        slider.transform.parent.GetChild(2).GetComponent<Text>().text = "Opacity : " + slider.value.ToString();
    }

    public void SetGSScale(UnityEngine.UI.Slider slider)
    {
        gsRenderer.m_SplatScale = slider.value;
        slider.transform.parent.GetChild(0).GetComponent<Text>().text = "Scale : " + slider.value.ToString();
    }

    public void SetGSSaturation(UnityEngine.UI.Slider slider)
    {
        gsRenderer.m_Saturation = slider.value;
        slider.transform.parent.GetChild(4).GetComponent<Text>().text = "Saturation : " + slider.value.ToString();        
    }

    public void SetGSPositionNoise(UnityEngine.UI.Slider slider)
    {
        noiseAmount = slider.value;
    }

    public void SetGSBlackAndWhite(UnityEngine.UI.Toggle toggle)
    {
        gsRenderer.m_IsBlackAndWhite = toggle.isOn;
    }

    public void SetGSLightened(UnityEngine.UI.Toggle toggle)
    {
        gsRenderer.m_Islightened = toggle.isOn;
    }

    public void SetGSOutline(UnityEngine.UI.Toggle toggle)
    {
        gsRenderer.m_IsOutlined = toggle.isOn;
    }

    public void SetGSAutoRotate(UnityEngine.UI.Toggle toggle)
    {
        gsRenderer.gameObject.GetComponent<Rotate>().enabled = toggle.isOn;
    }

    public void SetGSUpsideDown(UnityEngine.UI.Toggle toggle)
    {
        var rotation = gsRenderer.gameObject.transform.eulerAngles;
        rotation.x = 180;
        gsRenderer.gameObject.transform.eulerAngles = rotation;
    }

    public void DisplayColorPicker()
    {
        colorPicker.gameObject.SetActive(!colorPicker.gameObject.activeSelf);
    }

    public void SetGSSetOverColor(UnityEngine.UI.Button button)
    {
        gsRenderer.m_OverColor = colorPicker.CurrentColor;
        button.gameObject.GetComponent<UnityEngine.UI.Image>().color = colorPicker.CurrentColor;
    }

    public void ClosePanel()
    {
        openPanelGO.SetActive(true);
        var rectTransform = panelGO.GetComponent<RectTransform>();
        rectTransform.anchoredPosition = new Vector2(300, 0);
    }

    public void OpenPanel()
    {
        openPanelGO.SetActive(false);
        var rectTransform = panelGO.GetComponent<RectTransform>();
        rectTransform.anchoredPosition = new Vector2(0, 0);
    }

    // Norm16 do not work
    private void SetPositionNoise()
    {
        isComputing = true;

        if (gsRenderer.m_Asset != null)
        {
            unsafe
            {
                switch (gsRenderer.m_Asset.posFormat)
                {
                    case GaussianSplatAsset.VectorFormat.Float32:
                        for (int i = 0; i < gsRenderer.m_Asset.splatCount; i++)
                        {
                            byte* outputPtr = (byte*)m_OutputFloat32.GetUnsafePtr() + i * 12;

                            float maxNoise = noiseAmount;
                            *(float*)outputPtr = posFloatArray[i].x + UnityEngine.Random.Range(-maxNoise, maxNoise);
                            *(float*)(outputPtr + 4) = posFloatArray[i].y + UnityEngine.Random.Range(-maxNoise, maxNoise);
                            *(float*)(outputPtr + 8) = posFloatArray[i].z + UnityEngine.Random.Range(-maxNoise, maxNoise);
                        }

                        gsRenderer.m_GpuPosData.SetData(m_OutputFloat32);
                        break;
                    case GaussianSplatAsset.VectorFormat.Norm16:
                        for (int i = 0; i < gsRenderer.m_Asset.splatCount; i++)
                        {
                            float maxNoise = noiseAmount;
                            float3 position = posFloatArray[i];
                            position.x = posFloatArray[i].x + UnityEngine.Random.Range(-maxNoise, maxNoise);
                            position.y = posFloatArray[i].y + UnityEngine.Random.Range(-maxNoise, maxNoise);
                            position.z = posFloatArray[i].z + UnityEngine.Random.Range(-maxNoise, maxNoise);

                            byte* outputPtr = (byte*)m_OutputNorm16.GetUnsafePtr() + i * 6;
                            ulong enc = GaussianSplatAssetRuntimeCreator.EncodeFloat3ToNorm16(math.saturate(position));
                            *(uint*)outputPtr = (uint)enc;
                            *(ushort*)(outputPtr + 4) = (ushort)(enc >> 32);
                        }
                        gsRenderer.m_GpuPosData.SetData(m_OutputNorm16);
                        break;
                }
            }
        }
        else
        {
            // position of newly loaded ply
            float3[] newPositions = new float3[gsRenderer.splatCount];

            float maxNoise = noiseAmount;
            for (int i = 0; i < gsRenderer.splatCount; i++)
            {
                float x = BitConverter.ToSingle(posDataBytes, i * 12);
                newPositions[i].x = x;
                float y = BitConverter.ToSingle(posDataBytes, i * 12 + 4);
                newPositions[i].y = y;
                float z = BitConverter.ToSingle(posDataBytes, i * 12 + 8);
                newPositions[i].z = z;

                unsafe
                {
                    byte* outputPtr = (byte*)m_OutputFloat32.GetUnsafePtr() + i * 12;

                    *(float*)outputPtr = newPositions[i].x + UnityEngine.Random.Range(-maxNoise, maxNoise);
                    *(float*)(outputPtr + 4) = newPositions[i].y + UnityEngine.Random.Range(-maxNoise, maxNoise);
                    *(float*)(outputPtr + 8) = newPositions[i].z + UnityEngine.Random.Range(-maxNoise, maxNoise);   
                }
            }
            gsRenderer.m_GpuPosData.SetData(m_OutputFloat32);
        }
        isComputing = false;
    }

    public void StorePositionsForNewLoadedPly()
    {
        PositionNoiseSlider.value = 0;
        m_OutputFloat32.Dispose();
        m_OutputFloat32 = new NativeArray<byte>(gsRenderer.splatCount * 12, Allocator.Persistent);
        byte[] positions = new byte[gsRenderer.splatCount *4 * 3];
        gsRenderer.m_GpuPosData.GetData(positions);
        posDataBytes = positions;
    }
}
