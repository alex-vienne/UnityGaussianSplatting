using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using B83.Win32;
using UnityEngine.Events;
using GaussianSplatting.Runtime;
using System.Collections;

#if UNITY_EDITOR_WIN
using UnityEditor;
#endif

public class FileDragAndDrop : MonoBehaviour
{
    public UnityEvent m_onDropDetected;
    public string[] m_filesDropped;

    public GameObject LoadingIcon;
    private string[] m_currentPathsHold;

    private GaussianSplatRenderer gs;
    private GaussianSplatUI gsUI;

    private bool isLoading = false;

    public void Update()
    {
#if  UNITY_EDITOR_WIN
        string[] paths = DragAndDrop.paths;

        if (m_currentPathsHold !=null && m_currentPathsHold.FirstOrDefault() != m_filesDropped.FirstOrDefault())
        {
            if (paths != null && m_currentPathsHold != null && paths.Length == 0 && m_currentPathsHold.Length > 0)
            {
                m_filesDropped = m_currentPathsHold;
                m_onDropDetected.Invoke();
            }
        }
        m_currentPathsHold = paths;

#endif
    }

    void Awake()
    {
        UnityDragAndDropHook.InstallHook();
        UnityDragAndDropHook.OnDroppedFiles += OnFiles;
    }

    private void Start()
    {
        Debug.Log("FileDragAndDrop start");
        gs = FindFirstObjectByType<GaussianSplatRenderer>();
        gsUI = FindFirstObjectByType<GaussianSplatUI>();
        LoadingIcon.SetActive(false);
    }

    void OnDestroy()
    {
        UnityDragAndDropHook.UninstallHook();
    }

    void OnFiles(List<string> aFiles, POINT aPos)
    {
        if (isLoading == false)
        {
            isLoading = true;
            m_filesDropped = aFiles.ToArray();
            m_onDropDetected.Invoke();
        }
    }

    IEnumerator LoadPly()
    {
        Debug.Log("LoadPly file: " + this.m_filesDropped.FirstOrDefault());
        LoadingIcon.SetActive(true);
        yield return new WaitForEndOfFrame();
        // We should only read the screen buffer after rendering is complete
        if (gs.m_Asset != null)
        {
            gs.Reset(0);
            gs.m_Asset = null;
        }

        yield return new WaitForEndOfFrame();
        gs.CreateResourcesForAsset(this.m_filesDropped.FirstOrDefault());
        yield return new WaitForEndOfFrame();        
        gsUI.StorePositionsForNewLoadedPly();
        yield return new WaitForEndOfFrame();
        LoadingIcon.SetActive(false);
        isLoading = false;
    }

    public void DropPLYFile( )
    {
        StartCoroutine(LoadPly());
    }
}