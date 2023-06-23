using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class ConfigReader 
{
    private Dictionary<string, string> configData;
    public Dictionary<string, string> ConfigData
    {
        get
        {
            return configData;
        }
    }

    public ConfigReader()
    {
        configData = new Dictionary<string, string>();

        string text = File.ReadAllText(Application.streamingAssetsPath + "/config.txt");
        string[] configLines = text.Split('\n');

        foreach (string line in configLines)
        {
            string[] keyValue = line.Split('=');
            if (keyValue.Length == 2)
            {
                configData[keyValue[0].Trim()] = keyValue[1].Trim();
            }
        }

    }


}