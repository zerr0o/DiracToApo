using System.Text.Json;

namespace DiracToApo;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--worker")
        {
            WorkerRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<WorkerRequest>(File.ReadAllText(args[1]));
                if (request is null) throw new InvalidDataException("Requête de conversion vide.");
                WorkerHandshake.WaitForPermission(request);
                return VstCaptureWorker.Run(request);
            }
            catch (Exception ex)
            {
                if (request is not null)
                {
                    try { Directory.CreateDirectory(request.WorkDirectory); File.WriteAllText(Path.Combine(request.WorkDirectory, "failure.txt"), ex.Message); } catch { }
                }
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
        if (args.Length == 2 && args[0] == "--inspect")
        {
            try { Console.WriteLine(JsonSerializer.Serialize(DiracFilterInspector.Read(args[1]))); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        if (args.Length == 5 && args[0] == "--export-captured")
        {
            try
            {
                var result = JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(args[1])) ?? throw new InvalidDataException("Résultat vide.");
                var exported = ImpulseExporter.Export(result, args[2], args[3], args[4], new Progress<ConversionProgress>(), CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(exported)); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        }
        if (args.Length == 1 && args[0] == "--self-test")
            return ConverterSelfTests.Run();
        if (args.Length == 2 && args[0] == "--preview")
        {
            ApplicationConfiguration.Initialize();
            using var form = new MainForm(new ConversionService());
            using var timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                form.Close();
            };
            form.Shown += (_, _) => timer.Start();
            Application.Run(form);
            return 0;
        }
        if (args.Length != 0) return 2;
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(new ConversionService()));
        return 0;
    }
}
