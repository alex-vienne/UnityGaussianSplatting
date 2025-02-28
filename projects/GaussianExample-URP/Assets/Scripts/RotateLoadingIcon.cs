using UnityEngine;

public class RotateLoadingIcon : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        GetComponent<RectTransform>().Rotate(0, 0, -1);
    }
}
