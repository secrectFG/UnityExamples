using System.Collections;
using System.Collections.Generic;
using Test;
using UnityEngine;
using Network = Test.Network;
public class Main : MonoBehaviour
{
    Network network = new Network();

    class NetworkHandler : INetworkHandler
    {
        public string Filter => null;

        public void OnMsg(string name, byte[] data)
        {
            //Debug.Log($"OnMsg name:{name}");
        }
    }

    private void OnDestroy() {
        network.Close();
    }

    // Start is called before the first frame update
    IEnumerator Start()
    {
        network.IpList = new List<string>() {
            //"2406:da1e:2b:bc02:ad79:efaf:39f8:8f42"
            //"43.198.71.223",
            //"43.198.102.201"
            "game626a.com",
            "game626b.com",
            "game626c.com",

            };
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

        while (network.Logined==false)yield return null;

        int send = 0;
        int recv = 0;
        
        while (true)
        {
            yield return null;
            yield return new WaitForSeconds(0.3f);
            send ++;
            Debug.Log("Send KeepAliveReq "+send);
            
            network.SendReq(new CLGT.KeepAliveReq(), data=>{
                var ack = Network.PaserData<CLGT.KeepAliveAck>(data);
                 
                 recv ++;
                 Debug.Log($"KeepAliveAck "+recv);
            });
        }
    }

    // Update is called once per frame
    void Update()
    {
        network.Step();
    }
}
