using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;

public class PostBuild
{
    [PostProcessBuild(1000)]
    public static void OnPostprocessBuild(BuildTarget target, string pathToBuiltProject)
    {
        var path = @"\\192.168.101.221\shared\iOS打包\";
        if(!Directory.Exists(path)){
            Directory.CreateDirectory(path);
        }
        var copyToPathAfterBuild = path;

        var (outputFile, zipFileName) = ZipFilesToParentFolder(pathToBuiltProject);
        //复制到path
        EditorUtility.DisplayProgressBar($"正在拷贝到{copyToPathAfterBuild}", "", 0);
        
        File.Copy(outputFile, copyToPathAfterBuild + "/" + zipFileName, true);
        // int AllFileCount = Directory.GetFiles(pathToBuiltProject, "*", SearchOption.AllDirectories).Length;
        // int counter = 0;
        // CopyDirectory(pathToBuiltProject, copyToPathAfterBuild, f =>
        // {
        //     counter++;
        //     EditorUtility.DisplayProgressBar($"正在拷贝到{copyToPathAfterBuild}", $"{f}", (float)counter / AllFileCount);
        // });
        EditorUtility.ClearProgressBar();
    }

    static (string, string) ZipFilesToParentFolder(string inputFolder)
    {
        EditorUtility.DisplayProgressBar($"正在压缩...", "", 0);
        var dirinfo = new DirectoryInfo(inputFolder);
        var dirname = dirinfo.Name;
        var parentDir = dirinfo.Parent.FullName;
        var zipFileName = dirname + ".zip";
        var outputFile = Path.Combine(parentDir, zipFileName);


        using (var zipStream = new ZipOutputStream(File.Create(outputFile)))
        {
            zipStream.SetLevel(0); // 设置压缩级别 (0-9)

            int folderOffset = inputFolder.Length + (inputFolder.EndsWith("\\") ? 0 : 1);

            string[] files = Directory.GetFiles(inputFolder, "*.*", SearchOption.AllDirectories);
            var counter = 0;
            foreach (string filename in files)
            {
                FileInfo fileInfo = new FileInfo(filename);

                string entryName = dirname + "/" + filename.Substring(folderOffset);
                entryName = ZipEntry.CleanName(entryName);

                ZipEntry entry = new ZipEntry(entryName);
                entry.DateTime = fileInfo.LastWriteTime;
                entry.Size = fileInfo.Length;

                zipStream.PutNextEntry(entry);

                byte[] buffer = new byte[4096];
                using (FileStream streamReader = File.OpenRead(filename))
                {
                    StreamUtils.Copy(streamReader, zipStream, buffer);
                }
                zipStream.CloseEntry();
                counter++;
                EditorUtility.DisplayProgressBar($"正在压缩", $"{entryName}", (float)counter / files.Length);
            }

            zipStream.IsStreamOwner = true;
            zipStream.Close();
        }
        EditorUtility.ClearProgressBar();

        return (outputFile, zipFileName);
    }

    static void CopyDirectory(string sourceDirPath, string saveDirPath,
        System.Action<string> filehandler = null)
    {
        try
        {
            if (!Directory.Exists(saveDirPath))
            {
                Directory.CreateDirectory(saveDirPath);
            }
            string[] files = Directory.GetFiles(sourceDirPath);
            foreach (string file in files)
            {
                string pFilePath = saveDirPath + "/" + Path.GetFileName(file);
                if (File.Exists(pFilePath))
                    continue;
                File.Copy(file, pFilePath, true);
                filehandler?.Invoke(pFilePath);
            }

            string[] dirs = Directory.GetDirectories(sourceDirPath);
            foreach (string dir in dirs)
            {
                CopyDirectory(dir, saveDirPath + "/" + Path.GetFileName(dir), filehandler);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError(ex);
        }
    }
}
