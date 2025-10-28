using System;
using System.Diagnostics;
using System.IO;

namespace CSV_SYSTEM_API
{
    public static class SharedFolderHelper
    {
        /// <summary>
        /// 连接共享文件夹，连接上后可以像操作本地磁盘的方式操作文件夹和文件
        /// </summary>
        /// <param name="path">共享文件夹路径</param>
        /// <param name="userName">用户名</param>
        /// <param name="passWord">密码</param>
        /// <returns>连接成功返回true，否则返回false</returns>
        public static Tuple<bool,string> Connect(string path, string userName, string passWord)
        {
            bool Flag = false;
            string msg = string.Empty;
            Process proc = new Process();
            try
            {
                proc.StartInfo.FileName = "cmd.exe";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardInput = true;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.CreateNoWindow = true;
                proc.Start();
                string dosLine = @"net use " + path + " /User:" + userName + " " + passWord + " /PERSISTENT:YES";
                proc.StandardInput.WriteLine(dosLine);
                proc.StandardInput.WriteLine("exit");
                while (!proc.HasExited)
                {
                    proc.WaitForExit(1000);
                }
                string errormsg = proc.StandardError.ReadToEnd();
                proc.StandardError.Close();
                if (string.IsNullOrEmpty(errormsg))
                {
                    Flag = true;
                }
                else
                {
                    msg = errormsg;
                    Flag = false;
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {
                proc.Close();
                proc.Dispose();
            }
            return new Tuple<bool, string>(Flag, msg);
        }
    }
}
