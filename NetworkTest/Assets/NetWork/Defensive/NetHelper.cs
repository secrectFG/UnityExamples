using System;
using System.IO;
using System.Text;

namespace DefensiveNet
{
    public static class NetHelper
    {
        public class NetDataPack
        {
            public string packName;
            public byte[] pbdata;
        }
        /// <summary>
        /// CA3加密算法：高效快速，自带校验功能
        /// </summary>
        /// <param name="originContent">原始内容</param>
        /// <param name="randumKey">数字随机key</param>
        /// <returns>密文字符串</returns>
        public static string CA3Encode(string originContent, int randumKey)
        {
            byte[] content = System.Text.Encoding.UTF8.GetBytes(originContent);
            byte[] buffer = new byte[content.Length + 4];
            Array.Copy(BitConverter.GetBytes(randumKey), 0, buffer, 0, 4);
            Array.Copy(content, 0, buffer, 4, content.Length);

            int a = 12347, b = 20809, c = 65536;
            for (int i = 0; i < buffer.Length; ++i)
            {
                randumKey = (randumKey * a + b) % c;
                buffer[i] ^= (byte)(randumKey & 0xff);
            }

            return Convert.ToBase64String(buffer);
        }

        /// <summary>
        /// CA3解密算法：高效快速，自带校验功能
        /// </summary>
        /// <param name="encryptContent">密文</param>
        /// <param name="randumKey">数字随机key</param>
        /// <returns>原始内容</returns>
        public static string CA3Decode(string encryptContent, int randumKey)
        {
            byte[] buffer = Convert.FromBase64String(encryptContent);

            if (buffer.Length > 4)
            {
                int tmpKey = randumKey;

                int a = 12347, b = 20809, c = 65536;
                for (int i = 0; i < buffer.Length; ++i)
                {
                    randumKey = (randumKey * a + b) % c;
                    buffer[i] ^= (byte)(randumKey & 0xff);
                }

                int key = BitConverter.ToInt32(buffer, 0);
                if (key == tmpKey)
                    return System.Text.Encoding.UTF8.GetString(buffer, 4, buffer.Length - 4);
            }

            return string.Empty;
        }

        /// <summary>
        /// 安全地写入字符串，如果超过限制则写入空串！！
        /// </summary>
        public static void SafeWriteString(BinaryWriter bw, string content, int maxLength)
        {
            byte[] bcontent = Encoding.UTF8.GetBytes(content ?? string.Empty);
            if (bcontent.Length >= maxLength)
            {
                bw.Write((ushort)0);
                return;
            }
            bw.Write((ushort)bcontent.Length);
            bw.Write(bcontent);
        }
        /// <summary> 
        //时间函数
        /// <summary> 
        public static double GetTickCount()
        {
            TimeSpan timeSpan = (DateTime.UtcNow - new DateTime(1970, 1, 1));
            return timeSpan.TotalMilliseconds;
        }

        /// <summary>   
        /// Rc4算法，用于协议层加解密
        /// </summary>
        /// <param name="key">秘钥</param>
        /// <param name="data">数据</param>
        public static void Rc4Algorithm(byte[] key, byte[] buffer, int index, int length)
        {
            int key_len = key.Length;
            int data_len = length;

            int[] s = new int[256];
            int[] k = new int[256];

            int i = 0, j = 0, temp;

            for (i = 0; i < 256; i++)
            {
                s[i] = i;
                k[i] = key[i % key_len];
            }

            for (i = 0; i < 256; i++)
            {
                j = (j + s[i] + k[i]) & 0xff;
                temp = s[i];
                s[i] = s[j];
                s[j] = temp;
            }

            int x = 0, y = 0, t = 0;
            for (i = 0; i < data_len; i++)
            {
                x = (x + 1) & 0xff;
                y = (y + s[x]) & 0xff;
                temp = s[x];
                s[x] = s[y];
                s[y] = temp;
                t = (s[x] + s[y]) & 0xff;
                buffer[index + i] ^= (byte)s[t];
            }
        }



        //网络日志
        public static bool AllowNetLoger
        {
            get; set;
        } = true;
    }
}
