using GaussianSplatting.Runtime;
using HSVPicker;
using UnityEngine;
using UnityEngine.UI;
using static UnityEngine.GraphicsBuffer;

public class GaussianSplatUI : MonoBehaviour
{

    private GaussianSplatRenderer gaussianSplatRenderer;
    private ColorPicker colorPicker;
    private GameObject panelGO;
    private GameObject openPanelGO;

    void Start()
    {
        gaussianSplatRenderer = FindFirstObjectByType<GaussianSplatRenderer>();
        colorPicker = FindFirstObjectByType<ColorPicker>();
        colorPicker.gameObject.SetActive(false);
        panelGO = transform.GetChild(0).gameObject;
        openPanelGO = transform.GetChild(1).gameObject;
        openPanelGO.SetActive(false);
    }

    public void SetGSOpacity(Slider slider)
    {
        gaussianSplatRenderer.m_OpacityScale = slider.value;
    }

    public void SetGSScale(Slider slider)
    {
        gaussianSplatRenderer.m_SplatScale = slider.value;
    }

    public void SetGSSaturation(Slider slider)
    {
        gaussianSplatRenderer.m_Saturation = slider.value;
    }

    public void SetGSBlackAndWhite(Toggle toggle)
    {
        gaussianSplatRenderer.m_IsBlackAndWhite = toggle.isOn;
    }

    public void SetGSOutline(Toggle toggle)
    {
        gaussianSplatRenderer.m_IsOutlined = toggle.isOn;
    }

    public void SetGSAutoRotate(Toggle toggle)
    {
        gaussianSplatRenderer.gameObject.GetComponent<Rotate>().enabled = toggle.isOn;
    }

    public void DisplayColorPicker()
    {
        colorPicker.gameObject.SetActive(!colorPicker.gameObject.activeSelf);
    }

    public void SetGSSetOverColor(Button button)
    {
        gaussianSplatRenderer.m_OverColor = colorPicker.CurrentColor;
        button.gameObject.GetComponent<Image>().color = colorPicker.CurrentColor;
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
}
