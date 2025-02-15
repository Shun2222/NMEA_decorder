﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;

class Program
{
    static void Main(string[] args)
    {
        argparse ap = new argparse("東洋信号、JAXA、FileOut.txtのAISテキストファイルを読み込みデコードし、ais1ファイル形式で出力する。");
        ap.ResisterArgs(args);
        List<string> inFiles = ap.getArgs((char)0, "InFile", "入力ファイル", kind: argparse.Kind.FilesWithWildcard_or_FilesInFolder, canBeMulti: true);
        List<string> outFiles = ap.getArgs('o', "OutFile", "出力するais1ファイル（省略時は最初のInFileの拡張子をais1にしたもの）", kind: argparse.Kind.NotExistFile, canOmit: true);
        List<string> errFiles = ap.getArgs('e', "ErrorFile", "エラー出力ファイル（省略時はOutFileの拡張子をais1errにしたもの）", kind: argparse.Kind.NotExistFile, canOmit: true);
        List<string> Lines = ap.getArgs('l', "Lines", "読み込む最大行数（省略時は最後まで）", kind: argparse.Kind.IsInt, canOmit: true);

        if (ap.HasError) { Console.Error.Write(ap.ErrorOut()); return; }

        string outFilePath = (outFiles.Count() > 0) ? outFiles[0] : UnexistFilePath.ChangeExt(inFiles[0], "ais1");
        string errFilePath =(errFiles.Count()>0) ? errFiles[0]: UnexistFilePath.ChangeExt(outFilePath, "ais1err");
        int maxLine = (Lines.Count() > 0) ? int.Parse(Lines[0]) : int.MaxValue;

        //デコードしてファイルに書き出し
        using (StreamWriter swOut = new StreamWriter(outFilePath))
        using (StreamWriter swErr = new StreamWriter(errFilePath))
        {
            AISDecoding decorder = new AISDecoding();
            int seconds = DateTime.Now.Second;
            foreach (string infile in inFiles)
                using (StreamReader sr = new StreamReader(infile))
                {
                    int line = 0;
                    while (!sr.EndOfStream && line++ < maxLine)
                    {
                        string s = sr.ReadLine();
                        //進捗表示
                        if (DateTime.Now.Second != seconds)
                        {
                            seconds = DateTime.Now.Second;
                            Console.Error.WriteLine("{0:0}% in {1}/{2} Files", 100.0 * sr.BaseStream.Position / sr.BaseStream.Length, inFiles.IndexOf(infile) + 1, inFiles.Count());
                            swOut.Flush();
                            swErr.Flush();
                        }

                        //日時、!AIVDM部分の分離
                        DateTime dt;
                        string sentence;
                        if (GetAIVDsentence(s, out dt, out sentence))
                        {
                            //解読
                            AISCode code = decorder.setSentence(sentence);
                            switch (code.Result)
                            {
                                case enAISResult.IsSentenceErr:
                                case enAISResult.IsSequentialMessageInconsistentErr:
                                case enAISResult.IsDecapsulatingErr:
                                case enAISResult.IsOtherSentence:
                                case enAISResult.IsCheckSumErr:
                                    // デコードエラー
                                    swErr.WriteLine(code.Result.ToString() + "," + s);
                                    break;
                                case enAISResult.ToBeContinue:
                                    //行が継続する：do nothing
                                    break;
                                case enAISResult.IsDynamic:
                                    dynamicData D = code.DynamicData;
                                    if (D.Latitude == null | D.Longitude == null || D.SOG == null || D.COG == null || D.Heading == null) //緯度経度等が入ってない
                                        swErr.WriteLine("緯度経度等無効," + s);
                                    else
                                        swOut.WriteLine(
                                            dt.ToUniversalTime().ToString() + ",Dyn," +
                                            code.MMSI.ToString() + "," +
                                            D.NavigationStatus.ToString("00") + "," +
                                            ((double)D.Latitude).ToString("00.000000") + "," +
                                            ((double)D.Longitude).ToString("000.000000") + "," +
                                            ((double)D.COG).ToString("000.0") + "," +
                                            ((double)D.SOG).ToString("00.0") + "," +
                                            ((int)D.Heading).ToString("000") + "," +
                                            code.hash.ToString("x16")
                                            );
                                    break;
                                case enAISResult.IsStatic:
                                    staticData S = code.StaticData;
                                    swOut.WriteLine(
                                        dt.ToUniversalTime().ToString() + ",Sta," +
                                        code.MMSI.ToString() + "," +
                                        S.IMO_No.ToString() + "," +
                                        S.Name.Replace(',', '.').Replace('"', '\'').Trim(' ', '@') + "," +
                                        S.TypeOfShipAndCargo.ToString() + "," +
                                        S.LOA.ToString() + "," +
                                        S.Breadth.ToString() + "," +
                                        S.AntennaFromFore.ToString()+","+
                                        S.AntennaFromLeft.ToString()+","+
                                        S.draught + "," +
                                        S.ETA + "," +
                                        S.Destination.Replace(',', '.').Replace('"', '\'').Trim(' ', '@') + "," +
                                        code.hash.ToString("x16")
                                        );
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                }
        }
        Console.WriteLine(outFilePath);
    }

    static Regex r1 = new Regex(@"^(?<JST>\d+/\d+/\d+ \d+:\d+:\d+\.?\d*),[^!]*(?<sentence>!AIVD.+)$");  // Hub.exe記録型
    static Regex r2 = new Regex(@"^(?<UTC>\d{14}),(?<sentence>!AIVD.+)$");   // 東洋信号log型
    static Regex r3 = new Regex(@"(?<UTC>\d{14})000 00 (?<sentence>!AIVD.+)$");    //JAXA型

    static bool GetAIVDsentence(string line, out DateTime dt, out string sentence)
    {
        if (r1.IsMatch(line))
        {
            Match m = r1.Match(line); // Hub.exe記録型
            if (DateTime.TryParse(m.Groups["JST"].Value, out dt))
            {
                dt = DateTime.SpecifyKind(dt.AddHours(-9), DateTimeKind.Utc);
                sentence = m.Groups["sentence"].Value;
                return true;
            }
        }
        if (r2.IsMatch(line))  // 東洋信号log型
        {
            Match m = r2.Match(line);
            if (DateTime.TryParseExact(m.Groups["UTC"].Value, "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.AssumeUniversal, out dt))
            {
                //dt = DateTime.SpecifyKind(dt,DateTimeKind.Utc);//これをつけると値そのままでタイムゾーンだけ変わってしまう 12:00JST→12:00UTC
                sentence = m.Groups["sentence"].Value;
                return true;
            }
        }
        if (r3.IsMatch(line))  //JAXA型
        {
            Match m = r3.Match(line);
            if (DateTime.TryParseExact(m.Groups["UTC"].Value, "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.AssumeUniversal, out dt))
            {
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                sentence = m.Groups["sentence"].Value;
                return true;
            }
        }

        sentence = null;
        dt = DateTime.MinValue;
        return false;

    }
}

