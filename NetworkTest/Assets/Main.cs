using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DefensiveNet;
using Test;
using UnityEngine;
using Network = Test.Network;
public class Main : MonoBehaviour
{
    Network network = new Network();

    public static bool isNewMemoryStream = false;
    public static bool gc = false;

    public static bool adddataold = false;

    class NetworkHandler : INetworkHandler
    {
        public string Filter => null;

        public void OnMsg(string name, byte[] data)
        {
            //Debug.Log($"OnMsg name:{name}");
        }
    }

    private void OnDestroy()
    {
        network.Close();
    }



    // Start is called before the first frame update
    IEnumerator Start()
    {
        ConfigReader configReader = new ConfigReader();
        NetHelper.AllowNetLoger = true;
        //从streamingassets读取maxstart.txt，获取maxstart数值
        int maxStart = int.Parse(configReader.ConfigData["maxstart"]);
        var iplist = configReader.ConfigData["iplist"].Split(',');
        
        Debug.Log($"maxStart:{maxStart} iplist:{string.Join(",",iplist)}");
        network.maxStart = maxStart;
        network.IpList = iplist.ToList();
        network.port = 9000;
        network.Login("test", () =>
        {
            Debug.Log("loginok");
        }, status =>
        {
            Debug.Log($"status:{status}");
            if (status == "连接失败")
            {
                Debug.LogError("连接失败");
            }
            else
            {
                network.AddHanlder(new NetworkHandler());
            }
        }, useDirect: false);

        while (network.Logined == false) yield return null;

        int send = 0;
        int recv = 0;

        while (true)
        {
            yield return null;
            yield return new WaitForSeconds(0.3f);
            send++;

            if (bsend)
            {
                // Debug.Log("Send KeepAliveReq " + send);
                network.SendReq(new CLGT.KeepAliveReq(), data =>
                {
                    var ack = Network.PaserData<CLGT.KeepAliveAck>(data);

                    recv++;
                    // Debug.Log($"KeepAliveAck " + recv);
                });
            }

            // network.Send(new CLGT.KeepAliveReq());
        }
    }
    bool bsend = true;
    public void OnToggle(bool v)
    {
        bsend = v;
    }

    public void OnToggle_isNewMemoryStream(bool v){
        isNewMemoryStream = v;
    }

    public void OnToggle_gc(bool v){
        gc = v;
    }

    public void OnToggle_adddataold(bool v){
        adddataold = v;
    }

    // Update is called once per frame
    void Update()
    {
        network.Step();
    }
}
