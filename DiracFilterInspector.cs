using System.Buffers.Binary;
using System.Text;

namespace DiracToApo;

public sealed record FilterInfo(string Name, int[] SampleRates, string Sha256);

public static class DiracFilterInspector
{
    private sealed record Field(int Number, int Wire, ulong NumberValue, byte[] Data);
    public static FilterInfo Read(string path)
    {
        try { return ReadCore(path); }
        catch (OverflowException ex) { throw new InvalidDataException("Taille ou entier invalide dans le filtre Dirac.", ex); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Le fichier de filtre est tronqué.", ex); }
    }

    private static FilterInfo ReadCore(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 20 || stream.Length > 32 * 1024 * 1024)
            throw new InvalidDataException("Taille de filtre non prise en charge (32 Mo maximum).");
        byte[] data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        if (!data.AsSpan(0, 8).SequenceEqual("CARDRTRP"u8) || BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8, 8)) != 2)
            throw new InvalidDataException("Ce fichier n’est pas un export Dirac CARDRTRP version 2 reconnu. Un .bin miniDSP ou un WAV ne convient pas.");
        var top = Parse(data.AsSpan(16));
        string name = Path.GetFileNameWithoutExtension(path);
        var metadata = SingleField(top, 1, 2);
        if (metadata is not null)
        {
            var label = SingleField(Parse(metadata.Data), 2, 2);
            if (label is not null) name = Encoding.UTF8.GetString(label.Data);
        }
        var container = SingleField(top, 2, 2)
            ?? throw new InvalidDataException("Structure Dirac non reconnue : aucun bloc de traitement.");
        var program = SingleField(Parse(container.Data), 2, 2)
            ?? throw new InvalidDataException("Structure Dirac non reconnue : aucun programme de filtre.");
        var rates = new List<int>();
        foreach (var rateBlock in Parse(program.Data).Where(f => f.Number == 3 && f.Wire == 2))
        {
            var rateFields = Parse(rateBlock.Data);
            ulong sampleRate = SingleField(rateFields, 1, 0)?.NumberValue ?? 0;
            if (sampleRate is not (32000 or 44100 or 48000))
                throw new InvalidDataException("Ce filtre utilise une fréquence non prise en charge. Cette version accepte 32, 44,1 et 48 kHz.");
            var channelsContainer = SingleField(rateFields, 2, 2)
                ?? throw new InvalidDataException("Le filtre ne décrit pas ses canaux.");
            var channels = Parse(channelsContainer.Data).Where(f => f.Number == 1 && f.Wire == 2).ToArray();
            if (channels.Length != 2)
                throw new InvalidDataException("Cette version convertit uniquement les filtres stéréo, sans routage multicanal.");
            var pairs = new HashSet<(ulong, ulong)>();
            foreach (var channel in channels)
            {
                var fields = Parse(channel.Data);
                pairs.Add((SingleField(fields, 1, 0)?.NumberValue ?? 0,
                           SingleField(fields, 2, 0)?.NumberValue ?? 0));
            }
            if (!pairs.SetEquals([(0UL, 0UL), (1UL, 1UL)]))
                throw new InvalidDataException("Le routage de ce filtre n’est pas une correction stéréo gauche/droite indépendante.");
            rates.Add((int)sampleRate);
        }
        if (rates.Count == 0 || rates.Distinct().Count() != rates.Count)
            throw new InvalidDataException("La liste des fréquences du filtre est invalide.");
        return new FilterInfo(name, rates.Order().ToArray(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)));
    }

    private static Field? SingleField(List<Field> fields, int number, int wire)
    {
        var matches = fields.Where(f => f.Number == number).ToArray();
        if (matches.Length > 1 || (matches.Length == 1 && matches[0].Wire != wire))
            throw new InvalidDataException("Champ Dirac singulier dupliqué ou de type incorrect.");
        return matches.Length == 0 ? null : matches[0];
    }

    private static List<Field> Parse(ReadOnlySpan<byte> bytes)
    {
        var result = new List<Field>();
        int pos = 0;
        while (pos < bytes.Length)
        {
            ulong tag = Varint(bytes, ref pos);
            int number = checked((int)(tag >> 3));
            int wire = (int)(tag & 7);
            if (number <= 0 || number > 536870911 || result.Count >= 10000)
                throw new InvalidDataException("Structure du filtre invalide.");
            if (wire == 0) result.Add(new Field(number, wire, Varint(bytes, ref pos), []));
            else
            {
                int length = wire switch { 1 => 8, 5 => 4, 2 => checked((int)Varint(bytes, ref pos)), _ => throw new InvalidDataException("Encodage du filtre non reconnu.") };
                if (length < 0 || length > bytes.Length - pos) throw new InvalidDataException("Le fichier de filtre est tronqué.");
                result.Add(new Field(number, wire, 0, bytes.Slice(pos, length).ToArray()));
                pos += length;
            }
        }
        return result;
    }

    private static ulong Varint(ReadOnlySpan<byte> bytes, ref int pos)
    {
        ulong value = 0;
        for (int shift = 0; shift <= 63; shift += 7)
        {
            if (pos >= bytes.Length) throw new InvalidDataException("Le fichier de filtre est tronqué.");
            byte b = bytes[pos++];
            if (shift == 63 && (b & 0xfe) != 0) throw new InvalidDataException("Entier du filtre invalide.");
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Entier du filtre invalide.");
    }
}
