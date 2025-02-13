using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SimpleCameraController : MonoBehaviour
{

    public float mainSpeed = 1.0f; //regular speed
    public float shiftAdd = 250.0f; //multiplied by how long shift is held.  Basically running
    public float maxShift = 1000.0f; //Maximum speed when holdin gshift
    public float camSens = 0.25f; //How sensitive it with mouse
    public bool invertY = true;
    public float TranslationSpeed = 1.0f;

    public float scrollWheelSens = 1f;

    private Vector3 lastMouse = new Vector3(255, 255, 255); //kind of in the middle of the screen, rather than at the top (play)
    private float totalRun = 1.0f;

    private Vector3 p;

    void Update()
    {
        if (Input.GetMouseButton(1))
        {
            var mouseMoveY = invertY ? -1 * Input.GetAxis("Mouse Y") : Input.GetAxis("Mouse Y");
            var mouseMoveX = Input.GetAxis("Mouse X");

            var mouseMove = new Vector3(mouseMoveY, mouseMoveX, 0) * camSens;
            transform.eulerAngles = transform.eulerAngles + mouseMove;
        }

        if (Input.GetMouseButtonDown(1))
        {
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }

        if (Input.GetMouseButtonUp(1))
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        //Mouse  camera angle done.  

        //Keyboard commands
        float f = 0.0f;
        GetBaseInput();

        if (p.sqrMagnitude > 0)
        { // only move while a direction key is pressed
            if (Input.GetKey(KeyCode.LeftShift))
            {
                totalRun += Time.deltaTime;
                p = p * totalRun * shiftAdd;
                p.x = Mathf.Clamp(p.x, -maxShift, maxShift);
                p.y = Mathf.Clamp(p.y, -maxShift, maxShift);
                p.z = Mathf.Clamp(p.z, -maxShift, maxShift);
            }
            else
            {
                totalRun = Mathf.Clamp(totalRun * 0.5f, 1f, 1000f);
                p = p * mainSpeed;
            }

            p = p * Time.deltaTime;
            Vector3 newPosition = transform.position;
            if (Input.GetKey(KeyCode.Space))
            { //If player wants to move on X and Z axis only
                transform.Translate(p);
                newPosition.x = transform.position.x;
                newPosition.z = transform.position.z;
                transform.position = newPosition;
            }
            else
            {
                transform.Translate(p);
            }
        }

        var scroll = Input.GetAxis("Mouse ScrollWheel");
        mainSpeed += scroll * scrollWheelSens;
    }

    private void GetBaseInput()
    { //returns the basic values, if it's 0 than it's not active.
        if (Input.GetKey(KeyCode.UpArrow))
        {
            p += new Vector3(0, 0, 1);
        }
        if (Input.GetKey(KeyCode.DownArrow))
        {
            p += new Vector3(0, 0, -1);
        }
        if (Input.GetKey(KeyCode.LeftArrow))
        {
            p += new Vector3(-1, 0, 0);
        }
        if (Input.GetKey(KeyCode.RightArrow))
        {
            p += new Vector3(1, 0, 0);
        }
    }

    public void MoveForward()
    {
        p +=  new Vector3(0, 0, TranslationSpeed);
    }

    public void MoveBackward()
    {
        p += new Vector3(0, 0, -TranslationSpeed);
    }
}

