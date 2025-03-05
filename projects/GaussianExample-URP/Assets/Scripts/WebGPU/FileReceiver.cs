using GaussianSplatting.Runtime;
using System.Collections;
using UnityEngine;

public class FileReceiver : MonoBehaviour {

    public GameObject LoadingIcon;
    private GaussianSplatRenderer gs;
    private GaussianSplatUI gsUI;

    private bool isLoading = false;

    private void Start()
    {
        Debug.Log("FileReceiver start");
        gs = FindFirstObjectByType<GaussianSplatRenderer>();
        gsUI = FindFirstObjectByType<GaussianSplatUI>();
        LoadingIcon.SetActive(false);
    }

    private IEnumerator LoadFile(string url) {

        if (!isLoading)
        {
            isLoading = true;
            LoadingIcon.SetActive(true);
            var www = new WWW(url);
            yield return www;

            // Add your code here
            Debug.Log(www.bytes.Length + " bytes loaded");


            byte[] plyBytes = www.bytes;

            yield return new WaitForEndOfFrame();
            // We should only read the screen buffer after rendering is complete
            if (gs.m_Asset != null)
            {
                gs.Reset(0);
                gs.m_Asset = null;
            }

            yield return new WaitForEndOfFrame();
            gs.CreateResourcesForAsset(plyBytes);
            yield return new WaitForEndOfFrame();
            gsUI.StorePositionsForNewLoadedPly();
            yield return new WaitForEndOfFrame();
            LoadingIcon.SetActive(false);
            isLoading = false;
        }
    }

	public void FileSelected(string url) {
		StartCoroutine(LoadFile(url));
	}
}
