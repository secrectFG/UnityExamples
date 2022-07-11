using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public class TestEditor : EditorWindow
{

    [MenuItem("test/test")]
    static void Test()
    {
       var spr = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/bianlian/action/changeface/cover/action01.png");
    }


    
    static void CreateBuildWindow()
    {
        //var anim = GameObject.Find("Image").GetComponent<Animation>();
        AnimationClip clip = new AnimationClip { frameRate = 30 };
        //var clip = anim.clip;

        var objs = Selection.objects;

        var sprites = objs.Select(x => {
            var path = AssetDatabase.GetAssetPath(x);
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }).ToList();

       
        AnimationClipSettings setting = new AnimationClipSettings { loopTime = false };
        AnimationUtility.SetAnimationClipSettings(clip, setting);


        ObjectReferenceKeyframe[] keyframes = new ObjectReferenceKeyframe[sprites.Count];
        for (int i = 0; i < keyframes.Length; i++)
        {
            keyframes[i] = new ObjectReferenceKeyframe
            {
                value = sprites[i],
                time = i / 10.0f
            };
        }
        

        EditorCurveBinding curvebinding = EditorCurveBinding.PPtrCurve("down/Image", typeof(Image), "m_Sprite");
        AnimationUtility.SetObjectReferenceCurve(clip, curvebinding, keyframes);

        //Keyframe[] keys = new Keyframe[sprites.Count];
        //for (int i = 0; i < keyframes.Length; i++)
        //{
        //    keys[i] = new Keyframe
        //    {
        //        value = i,
        //        time = i / 10.0f
        //    };
        //}
        curvebinding = EditorCurveBinding.FloatCurve("up", typeof(RectTransform), "m_AnchoredPosition.x");


        var keyframes1 = new List<Keyframe>();
        for (int i = 0; i < keyframes.Length-1; i++)
        {
            var c = AnimationCurve.Constant(keyframes[i].time, keyframes[i+1].time, i);
            keyframes1.AddRange(c.keys);
        }

        var curve = new AnimationCurve(keyframes1.ToArray());
        AnimationUtility.SetEditorCurve(clip,curvebinding,curve);


        var savepath = "Assets/test/";
        AssetDatabase.CreateAsset(clip, savepath  + "test03" + ".anim");
        AssetDatabase.SaveAssets();

        //new GameObject().GetComponent<RectTransform>().anchoredPosition

        //GetWindow<TestEditor>();
        //var gobj = Selection.activeGameObject;
        //var clip = gobj.GetComponent<Animation>().clip;
        //var curve = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(Image), "Sprite"));
        //Debug.Log(curve);
        //AnimationUtility.SetEditorCurve(
        //        clip,
        //        EditorCurveBinding.PPtrCurve("2", typeof(Image), "Sprite"),
        //        curve
        //    );

    }

    public static TestEditor Create()
    {
        return GetWindow<TestEditor>();
    }
}
