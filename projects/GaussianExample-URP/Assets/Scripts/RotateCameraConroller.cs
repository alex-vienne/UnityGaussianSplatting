using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public class RotateCameraConroller : MonoBehaviour
{
    public float RotationSpeed = 0.2f;
    private SimpleCameraController simpleCameraController;

    [HideInInspector]
    public GameObject InitialPosition;

    // Start is called before the first frame update
    void Start()
    {
        InitialPosition = new GameObject();
        InitialPosition.transform.position = transform.position;
        InitialPosition.transform.rotation = transform.rotation;
        simpleCameraController = GetComponent<SimpleCameraController>();
    }

    public void MoveForwardOnClick()
    {
        simpleCameraController.MoveForward();
    }

    public void MoveBackwardOnClick()
    {
        simpleCameraController.MoveBackward();
    }

    public void RotateRightOnClick()
    {
        transform.Rotate(0, 1 * RotationSpeed, 0);
    }

    public void RotateLeftOnClick()
    {
        transform.Rotate(0, -1 * RotationSpeed, 0);
    }

    public void Center()
    {

    }
}
