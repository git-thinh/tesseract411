using System;
using System.Collections.Generic;
using System.Drawing;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Tesseract;
using System.Linq;
using Newtonsoft.Json;

class Program
{
    const int __PORT_WRITE = 1000;
    const int __PORT_READ = 1001;
    static RedisBase m_subcriber;
    static bool __running = true;

    static oTesseractRequest __ocrExecute(oTesseractRequest req, Bitmap image)
    {
        PageIteratorLevel level = PageIteratorLevel.Word;
        switch (req.command)
        {
            case TESSERACT_COMMAND.GET_SEGMENTED_REGION_BLOCK:
                level = PageIteratorLevel.Block;
                break;
            case TESSERACT_COMMAND.GET_SEGMENTED_REGION_PARA:
                level = PageIteratorLevel.Para;
                break;
            case TESSERACT_COMMAND.GET_SEGMENTED_REGION_SYMBOL:
                level = PageIteratorLevel.Symbol;
                break;
            case TESSERACT_COMMAND.GET_SEGMENTED_REGION_TEXTLINE:
                level = PageIteratorLevel.TextLine;
                break;
            case TESSERACT_COMMAND.GET_SEGMENTED_REGION_WORD:
                level = PageIteratorLevel.Word;
                break;
            case TESSERACT_COMMAND.GET_TEXT:
                break;
        }

        EngineMode mode = EngineMode.Default;
        switch (req.mode)
        {
            case ENGINE_MODE.LSTM_ONLY:
                mode = EngineMode.LstmOnly;
                break;
            case ENGINE_MODE.TESSERACT_AND_LSTM:
                mode = EngineMode.TesseractAndLstm;
                break;
            case ENGINE_MODE.TESSERACT_ONLY:
                mode = EngineMode.TesseractOnly;
                break;
        }

        using (var engine = new TesseractEngine(req.data_path, req.lang, mode))
        using (var pix = new BitmapToPixConverter().Convert(image))
        {
            using (var tes = engine.Process(pix))
            {
                switch (req.command)
                {
                    case TESSERACT_COMMAND.GET_TEXT:
                        string s = tes.GetText().Trim();
                        req.output_text = s;
                        req.output_count = s.Length;
                        req.ok = 1;
                        break;
                    default:
                        var boxes = tes.GetSegmentedRegions(level).Select(x =>
                            string.Format("{0}_{1}_{2}_{3}", x.X, x.Y, x.Width, x.Height)).ToArray();
                        req.output_format = "x_y_width_height";
                        req.output_text = string.Join("|", boxes.Select(x => x.ToString()).ToArray());
                        req.output_count = boxes.Length;
                        req.ok = 1;
                        break;
                }
            }
        }
        return req;
    }

    static void __executeBackground(byte[] buf)
    {
        oTesseractRequest r = null;
        string guid = Encoding.ASCII.GetString(buf);
        var redis = new RedisBase(new RedisSetting(REDIS_TYPE.ONLY_READ, __PORT_WRITE));
        try
        {
            string json = redis.HGET("_OCR_REQUEST", guid);
            r = JsonConvert.DeserializeObject<oTesseractRequest>(json);
            Bitmap bitmap = redis.HGET_BITMAP(r.redis_key, r.redis_field);
            if (bitmap != null) r = __ocrExecute(r, bitmap);
        }
        catch (Exception ex)
        {
            if (r != null)
            {
                string error = ex.Message + Environment.NewLine + ex.StackTrace
                    + Environment.NewLine + "----------------" + Environment.NewLine +
                   JsonConvert.SerializeObject(r);
                r.ok = -1;
                redis.HSET("_OCR_REQ_ERR", r.requestId, error);
            }
        }

        if (r != null)
        {
            redis.HSET("_OCR_REQUEST", r.requestId, JsonConvert.SerializeObject(r, Formatting.Indented));
            redis.HSET("_OCR_REQ_LOG", r.requestId, r.ok == -1 ? "-1" : r.output_count.ToString());
            redis.PUBLISH("__TESSERACT_OUT", r.requestId);
        }
    }

    static void __startApp()
    {
        m_subcriber = new RedisBase(new RedisSetting(REDIS_TYPE.ONLY_SUBCRIBE, __PORT_READ));
        m_subcriber.PSUBSCRIBE("__TESSERACT_IN");
        var bs = new List<byte>();
        while (__running)
        {
            if (!m_subcriber.m_stream.DataAvailable)
            {
                if (bs.Count > 0)
                {
                    var buf = m_subcriber.__getBodyPublish("__TESSERACT_IN", bs.ToArray());
                    bs.Clear();
                    new Thread(new ParameterizedThreadStart((o) => __executeBackground((byte[])o))).Start(buf);
                }
                Thread.Sleep(100);
                continue;
            }
            byte b = (byte)m_subcriber.m_stream.ReadByte();
            bs.Add(b);
        }
    }

    static void __stopApp() => __running = false;

    #region [ SETUP WINDOWS SERVICE ]

    static Thread __threadWS = null;
    static void Main(string[] args)
    {
        if (Environment.UserInteractive)
        {
            StartOnConsoleApp(args);
            Console.WriteLine("Press any key to stop...");
            Console.ReadKey(true);
            Stop();
        }
        else using (var service = new MyService())
                ServiceBase.Run(service);
    }

    public static void StartOnConsoleApp(string[] args) => __startApp();
    public static void StartOnWindowService(string[] args)
    {
        __threadWS = new Thread(new ThreadStart(() => __startApp()));
        __threadWS.IsBackground = true;
        __threadWS.Start();
    }

    public static void Stop()
    {
        __stopApp();
        if (__threadWS != null) __threadWS.Abort();
    }

    #endregion;
}

