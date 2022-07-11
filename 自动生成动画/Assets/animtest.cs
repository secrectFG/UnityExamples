using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[ExecuteInEditMode]
public class animtest : MonoBehaviour
{
    public Sprite[] sprites;
    // Start is called before the first frame update
    IEnumerator Start()
    {
        var img = GetComponent<Image>();
        yield return null;

        foreach (var item in sprites)
        {
            img.sprite = item;
            yield return new WaitForSeconds(0.5f);
        }
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    void OnGUI()
    {
        if (GUILayout.Button("test"))
        {
            print("test");
        }
    }
}
