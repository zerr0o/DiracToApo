using System.Text;

namespace DiracToApo;

public static class ConverterSelfTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "DiracToApo-selftests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        try
        {
            void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
            byte[] Var(ulong n) { using var s = new MemoryStream(); do { byte b = (byte)(n & 127); n >>= 7; s.WriteByte((byte)(b | (n > 0 ? 128 : 0))); } while (n > 0); return s.ToArray(); }
            byte[] Cat(params byte[][] arrays) => arrays.SelectMany(x => x).ToArray();
            byte[] Bytes(int field, byte[] payload) => Cat(Var((ulong)((field << 3) | 2)), Var((ulong)payload.Length), payload);
            byte[] Number(int field, ulong value) => Cat(Var((ulong)(field << 3)), Var(value));
            byte[] Filter(bool stereo = true, bool crossed = false, bool duplicate = false)
            {
                var channels = new List<byte[]> { Bytes(1, []) };
                if (stereo) channels.Add(Bytes(1, Cat(Number(1, 1), Number(2, crossed ? 0UL : 1UL), duplicate ? Number(1, 0) : [])));
                byte[] rate = Cat(Number(1, 48000), Bytes(2, Cat(channels.ToArray())));
                return Cat("CARDRTRP"u8.ToArray(), BitConverter.GetBytes(2UL), Bytes(1, Bytes(2, Encoding.UTF8.GetBytes("Test stéréo"))), Bytes(2, Bytes(2, Bytes(3, rate))));
            }
            string path = Path.Combine(root, "filter.bin");
            File.WriteAllBytes(path, Filter());
            var info = DiracFilterInspector.Read(path);
            Check(info.Name == "Test stéréo" && info.SampleRates.SequenceEqual([48000]) && info.Sha256.Length == 64, "inspect supported stereo container");
            void Reject(byte[] bytes, string name)
            {
                File.WriteAllBytes(path, bytes);
                try { DiracFilterInspector.Read(path); throw new Exception("Accepted invalid filter: " + name); }
                catch (InvalidDataException) { passed++; Console.WriteLine("PASS " + name); }
            }
            Reject(Filter(stereo: false), "reject mono");
            Reject(Filter(crossed: true), "reject channel mixing");
            Reject(Filter(duplicate: true), "reject duplicate singular channel fields");
            byte[] invalid = Filter(); invalid[0] = 0;
            Reject(invalid, "reject wrong signature");
            Reject(Filter()[..^1], "reject truncation");
            File.WriteAllBytes(path, Filter());
            try
            {
                new ConversionService().ConvertAsync(new(path, "missing.dll", root, 48000, false), new Progress<ConversionProgress>(), CancellationToken.None).GetAwaiter().GetResult();
                throw new Exception("Missing consent accepted");
            }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("Confirmez"), "no conversion without confirmation"); }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try
            {
                new ConversionService().ConvertAsync(new(path, "missing.dll", root, 48000, true), new Progress<ConversionProgress>(), cancellation.Token).GetAwaiter().GetResult();
                throw new Exception("Cancellation ignored");
            }
            catch (OperationCanceledException) { Check(true, "pre-cancelled request makes no changes"); }
            int dsp = DspSelfTests.Run();
            Check(dsp == 0, "DSP test suite");
            Check(ConversionService.RunRecoverySelfTests() == 0, "recovery and cancellation protections");
            Console.WriteLine($"PASS {passed} converter checks, plus DSP checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
