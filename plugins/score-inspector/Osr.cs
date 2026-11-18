// An osu! replay (.osr) header reader: everything before and after the compressed frames, so a plugin can look at
// a play without an LZMA library. The frames themselves stay compressed (FramesOffset / FramesLength point at them).
//
// Format: https://osu.ppy.sh/wiki/en/Client/File_formats/osr_%28file_format%29
// This file is the same in score-inspector/ and replay-check/ (each loose-source folder has to stand alone).

using System;
using System.IO;
using System.Text;

namespace TomYum.Examples.ScoreInspector;

/// <summary>A replay's header.</summary>
internal sealed record OsrInfo(
    int Mode,
    int Version,
    string BeatmapMd5,
    string PlayerName,
    string ReplayMd5,
    int Count300,
    int Count100,
    int Count50,
    int CountGeki,
    int CountKatu,
    int CountMiss,
    long TotalScore,
    int MaxCombo,
    bool Perfect,
    int Mods,
    DateTimeOffset? PlayedAt,
    int FramesOffset,
    int FramesLength,
    long OnlineScoreId)
{
    /// <summary>Accuracy (0-1) the way osu!(stable) works it out for <see cref="Mode"/>.</summary>
    public double Accuracy
    {
        get
        {
            double hit, max;
            switch (Mode)
            {
                case 1: // taiko
                    hit = Count300 + 0.5 * Count100;
                    max = Count300 + Count100 + CountMiss;
                    break;
                case 2: // catch: katu = missed droplets
                    hit = Count300 + Count100 + Count50;
                    max = hit + CountKatu + CountMiss;
                    break;
                case 3: // mania: geki = MAX, katu = 200
                    hit = 300.0 * (CountGeki + Count300) + 200.0 * CountKatu + 100.0 * Count100 + 50.0 * Count50;
                    max = 300.0 * (CountGeki + Count300 + CountKatu + Count100 + Count50 + CountMiss);
                    break;
                default:
                    hit = 300.0 * Count300 + 100.0 * Count100 + 50.0 * Count50;
                    max = 300.0 * (Count300 + Count100 + Count50 + CountMiss);
                    break;
            }
            return max <= 0 ? 0 : hit / max;
        }
    }
}

/// <summary>Reads <see cref="OsrInfo"/> from a complete .osr file.</summary>
internal static class Osr
{
    private static readonly string[] ModNames =
    [
        "NF", "EZ", "TD", "HD", "HR", "SD", "DT", "RX", "HT", "NC", "FL", "AT", "SO", "AP", "PF",
        "4K", "5K", "6K", "7K", "8K", "FI", "RD", "CN", "TP", "9K", "CO", "1K", "3K", "2K", "V2", "MR",
    ];

    /// <summary>Reads the header of <paramref name="data"/>.</summary>
    /// <exception cref="InvalidDataException">It isn't a replay.</exception>
    public static OsrInfo Read(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var r = new BinaryReader(stream, Encoding.UTF8);
            int mode = r.ReadByte();
            int version = r.ReadInt32();
            var beatmapMd5 = ReadString(r);
            var player = ReadString(r);
            var replayMd5 = ReadString(r);
            int c300 = r.ReadUInt16(), c100 = r.ReadUInt16(), c50 = r.ReadUInt16();
            int geki = r.ReadUInt16(), katu = r.ReadUInt16(), miss = r.ReadUInt16();
            long score = r.ReadInt32();
            int combo = r.ReadUInt16();
            bool perfect = r.ReadByte() != 0;
            int mods = r.ReadInt32();
            ReadString(r); // life bar graph
            long ticks = r.ReadInt64();
            int length = r.ReadInt32();
            int offset = checked((int)stream.Position);
            if (mode > 3 || beatmapMd5.Length != 32 || length < 0 || offset + length > data.Length)
                throw new InvalidDataException("not an osu! replay");
            stream.Position += length;

            // The online score id: an int64 since 2014-07-21, an int32 before (and absent on very old replays).
            long online = 0;
            long left = stream.Length - stream.Position;
            if (version >= 20140721 && left >= 8)
                online = r.ReadInt64();
            else if (left >= 4)
                online = r.ReadInt32();

            DateTimeOffset? at = ticks > 0 && ticks < DateTime.MaxValue.Ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
            return new OsrInfo(mode, version, beatmapMd5, player, replayMd5, c300, c100, c50, geki, katu, miss,
                score, combo, perfect, mods, at, offset, length, online);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("the replay is cut short", e);
        }
    }

    /// <summary>Legacy mod bits as text, e.g. <c>HDDT</c> (NC/PF hide the DT/SD they come with); <c>NM</c> for none.</summary>
    public static string ModsText(int mods)
    {
        if ((mods & 512) != 0) mods &= ~64;     // NC includes DT
        if ((mods & 16384) != 0) mods &= ~32;   // PF includes SD
        var sb = new StringBuilder();
        for (int bit = 0; bit < ModNames.Length; bit++)
        {
            if ((mods & (1 << bit)) != 0)
                sb.Append(ModNames[bit]);
        }
        return sb.Length == 0 ? "NM" : sb.ToString();
    }

    /// <summary>A ruleset's name for a stable mode number.</summary>
    public static string ModeName(int mode) => mode switch
    {
        1 => "taiko",
        2 => "catch",
        3 => "mania",
        _ => "osu!",
    };

    // osu!'s strings: 0x00 for none, or 0x0b and a ULEB128 length (what BinaryReader.ReadString reads).
    private static string ReadString(BinaryReader r) => r.ReadByte() switch
    {
        0x00 => string.Empty,
        0x0b => r.ReadString(),
        var b => throw new InvalidDataException($"bad string marker 0x{b:x2}"),
    };
}
