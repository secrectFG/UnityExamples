using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;


namespace Convertor
{
    [System.Serializable]
    class Skin
    {
        public string value;
        public int index;
    }

    [System.Serializable]
    class MoreData<T>
    {
        public T value;
        public int index;
    }

    [System.Serializable]
    class KeyFrame
    {
        //public XY[] x;
        //public XY[] y;

        public Skin[] skin;
        public MoreData<int>[] alpha;
    }

    [System.Serializable]
    class Note
    {
        public KeyFrame keyframes;
    }


    [System.Serializable]
    class AnimationData
    {
        public string name;
        public float frameRate;
        public Note[] nodes;
    }

    [System.Serializable]
    class JsonData
    {
        public AnimationData[] animations;
    }

    public class Convertor
    {
        [MenuItem("转换/开始")]
        static void Run()
        {
            var path = Application.dataPath + "/bianlian/";
            var jsonfilePath = path + "ChangeFace.ani";

            var jdata = JsonUtility.FromJson<JsonData>(File.ReadAllText(jsonfilePath));
            foreach (var anim in jdata.animations)
            {
                //if (anim.name == "Start2")
                {
                    ConvertAnim(anim);
                }
            }
        }

        static void ConvertAnim(AnimationData animationData)
        {
            AnimationClip clip = new AnimationClip { frameRate = 30 };


            ObjectReferenceKeyframe[] handleOneLayer(KeyFrame layer, string pathname, ObjectReferenceKeyframe[] layer1keyframes=null)
            {
                if (layer.skin == null) return null;
                ObjectReferenceKeyframe[] keyframes = new ObjectReferenceKeyframe[layer.skin.Length];
                var sprites = layer.skin.Select(skin => AssetDatabase.LoadAssetAtPath<Sprite>("Assets/" + skin.value)).ToList();

                var setting = new AnimationClipSettings { loopTime = false };
                AnimationUtility.SetAnimationClipSettings(clip, setting);

                for (int i = 0; i < keyframes.Length; i++)
                {
                    keyframes[i] = new ObjectReferenceKeyframe
                    {
                        value = sprites[i],
                        time = i / animationData.frameRate,
                    };
                }

                EditorCurveBinding curvebinding = EditorCurveBinding.PPtrCurve(pathname, typeof(Image), "m_Sprite");
                AnimationUtility.SetObjectReferenceCurve(clip, curvebinding, keyframes);

                //curvebinding = EditorCurveBinding.PPtrCurve(pathname, typeof(GameObject), "m_IsActive");
                var goBinding = new EditorCurveBinding();
                goBinding.type = typeof(GameObject);
                goBinding.propertyName = "m_IsActive";
                goBinding.path = pathname;
                if (layer.alpha != null)
                {
                    List<Keyframe> keyframes1 = new List<Keyframe>();
                    //int last = 1;
                    float lastTime = 0;
                    for (int i = 0; i < layer1keyframes.Length; i++)
                    {
                        var d = layer.alpha.FirstOrDefault(x=>x.index == i);
                        if (d != null)
                        {
                            var timeSum = layer1keyframes.Where((x,index)=>index<=i).Sum(x=>x.time);
                            //last = d.index;
                            Debug.Log($"i:{i} value:{d.value} {lastTime} {timeSum}");
                            var c = AnimationCurve.Constant(lastTime, timeSum, d.value==0?1:0);//d.value需要反转，因为不知道为什么后面设置Constant的时候它会反转
                            keyframes1.AddRange(c.keys);
                            lastTime = timeSum;
                        }
                    }
                    var curve = new AnimationCurve(keyframes1.ToArray());
                    //for (int i = 0; i < curve.length; i++)
                    {
                        //AnimationUtility.SetKeyBroken(curve,1,true);
                        //AnimationUtility.SetKeyLeftTangentMode(curve, 1, AnimationUtility.TangentMode.Constant);
                    }

                    AnimationUtility.SetEditorCurve(clip, goBinding, curve);
                    //curve = AnimationUtility.GetEditorCurve(clip, goBinding);

                    for (int k = 0; k < curve.keys.Length; ++k)
                    {
                        AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.Constant);
                        AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.Constant);
                    }
                    AnimationUtility.SetEditorCurve(clip, goBinding, curve);

                }
                return keyframes;
            }

            var layer1keyframes = handleOneLayer(animationData.nodes[0].keyframes, "avatar");
            if (layer1keyframes == null) return;
            handleOneLayer(animationData.nodes[1].keyframes, "face", layer1keyframes);



            var savepath = "Assets/bianlian/";
            AssetDatabase.CreateAsset(clip, savepath + animationData.name + ".anim");
            AssetDatabase.SaveAssets();
            //AssetDatabase.Refresh();
            Debug.Log($"已完成 {animationData.name}");
        }

        
    }
}