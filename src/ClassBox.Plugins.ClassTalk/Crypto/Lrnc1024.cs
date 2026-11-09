using System.IO;
using System.Security.Cryptography;

namespace ClassBox.Plugins.ClassTalk.Crypto;

// =============================================================
// LRNC-1024-AEAD v0.2 — C# 移植（修复版）
// 移植自 D:\classtalk\加密\lrnc1024_aead（主 py + 三补丁）
// 相对 Python 参考实现的改进：
//  1. 轮函数改为可逆 ARX（ChaCha 类 QR + 轮密钥/常数），修复参考实现
//     _mix_words / _layer_decrypt 数学上不可逆、解密无法还原的问题；
//  2. 块串联改为标准 CFB（掩码 = E(state)，C_{i+1} = C_i），修复补丁版
//     decrypt 恢复 padded = D(C) XOR C 不等于明文、末块状态不可恢复的问题；
//  3. 恒定时间比较 aad_mac / tag / 长度承诺；
//  4. 会话级 nonce 自动生成与重用目录；5. PBKDF2 口令派生。
// =============================================================

public class LrncError : Exception
{
    public LrncError(string message) : base(message) { }
}

public sealed class ParameterError : LrncError
{
    public ParameterError(string message) : base(message) { }
}

public sealed class AuthenticationError : LrncError
{
    public AuthenticationError(string message) : base(message) { }
}

public sealed class StateError : LrncError
{
    public StateError(string message) : base(message) { }
}

public static class LrncConstants
{
    public const int StateWords = 16;
    public const int BlockBytes = 128;
    public const int RoundsPerLayer = 20;
    public const int DefaultLayers = 4;
    public const int TagBytes = 32;
    public const int NonceBytes = 12;
    public const int KeyBytes = 32;
    public const int AadMacBytes = 64;
    public const int LengthSaltBytes = 32;
    public const int LengthCommitBytes = 64;
    public const long MaxMessageBytes = 1L << 32;
    public const int Version = 2;

    public static readonly byte[] PaddingDomain = "LRNC1024-AEAD-Padding-v0.2"u8.ToArray();
    public static readonly byte[] LengthDomain = "LRNC1024-AEAD-LengthCommit-v0.2"u8.ToArray();
    public static readonly byte[] CommitDomain = "LRNC1024-AEAD-Commit-v0.2"u8.ToArray();
    public static readonly byte[] KdfDomain = "LRNC1024-AEAD-KDF-v0.2"u8.ToArray();
    public static readonly byte[] AadDomain = "LRNC1024-AEAD-AAD-v0.2"u8.ToArray();
    public static readonly byte[] BlockDomain = "Block"u8.ToArray();
    public static readonly byte[] LayerEncryptDomain = "LayerEncrypt"u8.ToArray();
    public static readonly byte[] AlgorithmId = "LRNC-1024-AEAD"u8.ToArray();

    public static readonly ulong[] RoundConstants =
    {
        0x243F6A8885A308D3, 0x13198A2E03707344, 0xA4093822299F31D0,
        0x082EFA98EC4E6C89, 0x452821E638D01377, 0xBE5466CF34E90C6C,
        0xC0AC29B7C97C50DD, 0x3F84D5B5B5470917, 0x9216D5D98979FB1B,
        0xD1310BA698DFB5AC, 0x2FFD72DBD01ADFB7, 0xB8E1AFED6A267E96,
        0xBA7C9045F12C7F99, 0xC63C3B68B21D4EE9, 0x71A3D9D1C51D445E,
        0x64B4D1D9D1C51D45, 0x1032547698BADCFE, 0xEFCDAB9078563412,
        0xC3D2E1F0B8A79654, 0x8B6D5C4E3F2A1908,
    };
}

public static class LrncPrimitives
{
    // ---------- 字节序 ----------
    public static byte[] U16Le(ushort v) => new[] { (byte)v, (byte)(v >> 8) };
    public static byte[] U32Le(uint v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };
    public static byte[] U64Le(ulong v)
    {
        var b = new byte[8];
        for (int i = 0; i < 8; i++) b[i] = (byte)(v >> (8 * i));
        return b;
    }

    public static byte[] Le64Pack(ulong[] words)
    {
        var outBytes = new byte[LrncConstants.BlockBytes];
        for (int i = 0; i < words.Length; i++)
        {
            ulong w = words[i];
            int baseIdx = i * 8;
            for (int k = 0; k < 8; k++) outBytes[baseIdx + k] = (byte)(w >> (8 * k));
        }
        return outBytes;
    }

    public static ulong[] Le64Unpack(byte[] data)
    {
        if (data.Length != LrncConstants.BlockBytes) throw new ParameterError("状态块必须为 128 字节");
        var words = new ulong[LrncConstants.StateWords];
        for (int i = 0; i < LrncConstants.StateWords; i++)
        {
            int b = i * 8;
            ulong w = data[b] | ((ulong)data[b + 1] << 8) | ((ulong)data[b + 2] << 16) | ((ulong)data[b + 3] << 24)
                | ((ulong)data[b + 4] << 32) | ((ulong)data[b + 5] << 40) | ((ulong)data[b + 6] << 48) | ((ulong)data[b + 7] << 56);
            words[i] = w;
        }
        return words;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var r = new byte[total];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }

    public static byte[] Xor(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) throw new ParameterError("XOR 长度不一致");
        var r = new byte[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = (byte)(a[i] ^ b[i]);
        return r;
    }

    public static byte[] Join(byte[][] blocks)
    {
        int total = 0;
        foreach (var b in blocks) total += b.Length;
        var r = new byte[total];
        int o = 0;
        foreach (var b in blocks) { b.CopyTo(r, o); o += b.Length; }
        return r;
    }

    public static byte[][] Split(byte[] data, int size)
    {
        if (data.Length % size != 0) throw new ParameterError("数据长度不是块大小的整数倍");
        var blocks = new byte[data.Length / size][];
        for (int i = 0; i < blocks.Length; i++)
        {
            blocks[i] = new byte[size];
            Array.Copy(data, i * size, blocks[i], 0, size);
        }
        return blocks;
    }

    // ---------- 恒定时间比较 ----------
    public static bool ConstantTimeEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        int diff = 0;
        for (int i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
        return diff == 0;
    }

    // ---------- 哈希 / HMAC ----------
    public static byte[] Sha256(byte[] data) => SHA256.HashData(data);

    public static byte[] HmacSha512(byte[] key, byte[] data)
    {
        using var h = new HMACSHA512(key);
        return h.ComputeHash(data);
    }

    public static byte[] HmacSha512First(byte[] key, byte[] data, int count)
    {
        var full = HmacSha512(key, data);
        var r = new byte[count];
        Array.Copy(full, r, count);
        return r;
    }

    // ---------- KDF（HMAC-SHA512 计数器模式） ----------
    public static byte[] Kdf(byte[] key, byte[] domain, uint counter, int length)
    {
        if (key.Length != LrncConstants.KeyBytes) throw new ParameterError("层密钥必须为 32 字节");
        if (counter > 0xFFFFFFFF) throw new ParameterError("KDF 计数器越界");
        if (length <= 0 || length > 2048) throw new ParameterError("KDF 输出长度越界");
        var output = new List<byte>(length + 64);
        while (output.Count < length)
        {
            uint cb = counter + (uint)(output.Count / 64);
            var msg = Concat(domain, U32Le(cb));
            output.AddRange(HmacSha512(key, msg));
        }
        var result = new byte[length];
        output.CopyTo(0, result, 0, length);
        return result;
    }

    // ---------- 域函数 ----------
    public static byte[] AadMac(byte[] key, byte[] nonce, byte[] aad)
    {
        if (key.Length != LrncConstants.KeyBytes) throw new ParameterError("AAD MAC 密钥宽度错误");
        return HmacSha512First(key, Concat(LrncConstants.AadDomain, nonce, aad), LrncConstants.AadMacBytes);
    }

    public static byte[] AuthKey(byte[] layerKey, byte[] commitment, byte[] nonce)
        => Kdf(layerKey, Concat(LrncConstants.CommitDomain, nonce, commitment), 0, LrncConstants.KeyBytes);

    // ---------- 填充 / 吸收 ----------
    public static byte[] Pad(byte[] message)
    {
        if ((long)message.Length > LrncConstants.MaxMessageBytes) throw new ParameterError("消息超过实现上限");
        if ((long)message.Length == LrncConstants.MaxMessageBytes) throw new ParameterError("消息长度必须比实现上限至少保留一个填充字节");
        var padded = new List<byte>(message.Length + LrncConstants.BlockBytes);
        padded.AddRange(message);
        padded.Add(0x80);
        while (padded.Count % LrncConstants.BlockBytes != 0) padded.Add(0x00);
        return padded.ToArray();
    }

    public static byte[][] Absorb(byte[] message)
    {
        if ((long)message.Length > LrncConstants.MaxMessageBytes) throw new ParameterError("消息超过实现上限");
        if (message.Length == 0) return new[] { new byte[LrncConstants.BlockBytes] };
        int blockCount = (message.Length + LrncConstants.BlockBytes - 1) / LrncConstants.BlockBytes;
        var blocks = new byte[blockCount][];
        for (int i = 0; i < blockCount; i++)
        {
            int start = i * LrncConstants.BlockBytes;
            int count = Math.Min(LrncConstants.BlockBytes, message.Length - start);
            var b = new byte[LrncConstants.BlockBytes];
            Array.Copy(message, start, b, 0, count);
            blocks[i] = b;
        }
        return blocks;
    }

    public static (byte[] Message, int Length) Unpad(byte[] padded)
    {
        if (padded.Length % LrncConstants.BlockBytes != 0) throw new StateError("填充后的数据不是完整状态块序列");
        if (padded.Length == 0) throw new StateError("填充消息不能为空");
        int index = padded.Length - 1;
        while (index >= 0 && padded[index] != 0x80) index--;
        if (index < 0) throw new StateError("填充标记缺失");
        var message = new byte[index];
        Array.Copy(padded, message, index);
        if ((long)message.Length > LrncConstants.MaxMessageBytes) throw new StateError("恢复的消息长度超过实现上限");
        return (message, message.Length);
    }

    // ---------- 长度承诺 ----------
    public static byte[] LengthCommitment(byte[] key, int length, byte[] salt)
    {
        if (key.Length != LrncConstants.KeyBytes) throw new ParameterError("长度承诺密钥宽度错误");
        if (salt.Length != LrncConstants.LengthSaltBytes) throw new ParameterError("长度承诺盐必须为 32 字节");
        if (length < 0 || (long)length > LrncConstants.MaxMessageBytes) throw new ParameterError("长度承诺范围错误");
        var len = (ulong)length;
        var serialized = Concat(U64Le(len), U64Le(len >> 32), salt);
        return HmacSha512First(key, Concat(LrncConstants.LengthDomain, serialized), LrncConstants.LengthCommitBytes);
    }

    public static (byte[] Salt, byte[] Commit) CreateLengthBinding(byte[] layerKey, byte[] commitmentKey, int length)
    {
        var salt = RandomNumberGenerator.GetBytes(LrncConstants.LengthSaltBytes);
        var commit = LengthCommitment(layerKey, length, salt);
        return (salt, commit);
    }

    public static void VerifyLengthBinding(byte[] commit, byte[] salt, byte[] recoveredMessage, byte[] layerKey)
    {
        if (commit.Length != LrncConstants.LengthCommitBytes) throw new StateError("长度承诺必须为 64 字节");
        if (salt.Length != LrncConstants.LengthSaltBytes) throw new StateError("长度承诺盐必须为 32 字节");
        var expected = LengthCommitment(layerKey, recoveredMessage.Length, salt);
        if (!ConstantTimeEqual(expected, commit)) throw new StateError("明文长度承诺校验失败");
    }

    // ---------- 可逆 ARX 轮函数（修复版） ----------
    private static ulong Rotl(ulong x, int s) => (x << s) | (x >> (64 - s));

    private static void Qr(ulong[] s, int a, int b, int c, int d)
    {
        s[a] += s[b]; s[d] ^= s[a]; s[d] = Rotl(s[d], 16);
        s[c] += s[d]; s[b] ^= s[c]; s[b] = Rotl(s[b], 12);
        s[a] += s[b]; s[d] ^= s[a]; s[d] = Rotl(s[d], 8);
        s[c] += s[d]; s[b] ^= s[c]; s[b] = Rotl(s[b], 7);
    }

    private static void IQr(ulong[] s, int a, int b, int c, int d)
    {
        s[b] = Rotl(s[b], 57); s[b] ^= s[c]; s[c] -= s[d];
        s[d] = Rotl(s[d], 56); s[d] ^= s[a]; s[a] -= s[b];
        s[b] = Rotl(s[b], 52); s[b] ^= s[c]; s[c] -= s[d];
        s[d] = Rotl(s[d], 48); s[d] ^= s[a]; s[a] -= s[b];
    }

    public static ulong[] MixWords(ulong[] state, ulong[] keyWords)
    {
        if (state.Length != LrncConstants.StateWords || keyWords.Length != LrncConstants.StateWords)
            throw new StateError("状态和轮密钥必须包含 16 个字");
        var ns = (ulong[])state.Clone();
        for (int ri = 0; ri < LrncConstants.RoundsPerLayer; ri++)
        {
            for (int i = 0; i < LrncConstants.StateWords; i++)
            {
                ns[i] += keyWords[(i + ri) % LrncConstants.StateWords];
                ns[i] += LrncConstants.RoundConstants[(ri + i) % LrncConstants.RoundConstants.Length];
            }
            Qr(ns, 0, 4, 8, 12); Qr(ns, 1, 5, 9, 13); Qr(ns, 2, 6, 10, 14); Qr(ns, 3, 7, 11, 15);
            Qr(ns, 0, 5, 10, 15); Qr(ns, 1, 6, 11, 12); Qr(ns, 2, 7, 8, 13); Qr(ns, 3, 4, 9, 14);
        }
        return ns;
    }

    public static ulong[] MixWordsInverse(ulong[] state, ulong[] keyWords)
    {
        if (state.Length != LrncConstants.StateWords || keyWords.Length != LrncConstants.StateWords)
            throw new StateError("状态和轮密钥必须包含 16 个字");
        var ns = (ulong[])state.Clone();
        for (int ri = LrncConstants.RoundsPerLayer - 1; ri >= 0; ri--)
        {
            IQr(ns, 0, 5, 10, 15); IQr(ns, 1, 6, 11, 12); IQr(ns, 2, 7, 8, 13); IQr(ns, 3, 4, 9, 14);
            IQr(ns, 0, 4, 8, 12); IQr(ns, 1, 5, 9, 13); IQr(ns, 2, 6, 10, 14); IQr(ns, 3, 7, 11, 15);
            for (int i = 0; i < LrncConstants.StateWords; i++)
            {
                ns[i] -= keyWords[(i + ri) % LrncConstants.StateWords];
                ns[i] -= LrncConstants.RoundConstants[(ri + i) % LrncConstants.RoundConstants.Length];
            }
        }
        return ns;
    }

    public static ulong[] LayerEncrypt(ulong[] state, byte[] layerKey, int layer)
    {
        if (state.Length != LrncConstants.StateWords) throw new StateError("状态宽度错误");
        if (layerKey.Length != LrncConstants.KeyBytes) throw new ParameterError("层密钥宽度错误");
        var material = Kdf(layerKey, Concat(LrncConstants.LayerEncryptDomain, new[] { (byte)layer }), 0, 256);
        var roundKey = Le64Unpack(material[..128]);
        var whitening = Le64Unpack(material[128..]);
        var st = new ulong[LrncConstants.StateWords];
        for (int i = 0; i < LrncConstants.StateWords; i++) st[i] = state[i] ^ whitening[i];
        st = MixWords(st, roundKey);
        var result = new ulong[LrncConstants.StateWords];
        for (int i = 0; i < LrncConstants.StateWords; i++) result[i] = st[i] ^ whitening[LrncConstants.StateWords - 1 - i];
        return result;
    }

    public static ulong[] LayerDecrypt(ulong[] state, byte[] layerKey, int layer)
    {
        if (state.Length != LrncConstants.StateWords) throw new StateError("状态宽度错误");
        if (layerKey.Length != LrncConstants.KeyBytes) throw new ParameterError("层密钥宽度错误");
        var material = Kdf(layerKey, Concat(LrncConstants.LayerEncryptDomain, new[] { (byte)layer }), 0, 256);
        var roundKey = Le64Unpack(material[..128]);
        var whitening = Le64Unpack(material[128..]);
        var st = new ulong[LrncConstants.StateWords];
        for (int i = 0; i < LrncConstants.StateWords; i++) st[i] = state[i] ^ whitening[LrncConstants.StateWords - 1 - i];
        st = MixWordsInverse(st, roundKey);
        var result = new ulong[LrncConstants.StateWords];
        for (int i = 0; i < LrncConstants.StateWords; i++) result[i] = st[i] ^ whitening[i];
        return result;
    }
}

public sealed class Lrnc1024KeySet
{
    public byte[][] LayerKeys { get; }
    public byte[][] Commitments { get; }
    public int Layers => LayerKeys.Length;

    private Lrnc1024KeySet(byte[][] layerKeys, byte[][] commitments)
    {
        LayerKeys = layerKeys;
        Commitments = commitments;
    }

    public static Lrnc1024KeySet FromMasterSecret(byte[] masterSecret, int layers = LrncConstants.DefaultLayers)
    {
        if (masterSecret.Length != LrncConstants.KeyBytes) throw new ParameterError("主密钥必须为 32 字节");
        if (layers < 1 || layers > 255) throw new ParameterError("层数必须在 1 至 255 之间");
        var layerKeys = new byte[layers][];
        var commitments = new byte[layers][];
        for (int layer = 0; layer < layers; layer++)
        {
            var material = LrncPrimitives.HmacSha512(masterSecret,
                LrncPrimitives.Concat(LrncConstants.KdfDomain, new[] { (byte)layer }, masterSecret));
            layerKeys[layer] = material[..LrncConstants.KeyBytes];
            commitments[layer] = material[LrncConstants.KeyBytes..(LrncConstants.KeyBytes + 32)];
        }
        return new Lrnc1024KeySet(layerKeys, commitments);
    }

    public (byte[] Key, byte[] Commitment) GetLayer(int layer)
    {
        if (layer < 0 || layer >= Layers) throw new ParameterError("层索引越界");
        return (LayerKeys[layer], Commitments[layer]);
    }
}

public sealed class Lrnc1024Encrypted
{
    public string Algorithm { get; }
    public int Version { get; }
    public int Layers { get; }
    public byte[] Nonce { get; }
    public byte[] AadMac { get; }
    public byte[] LengthSalt { get; }
    public byte[] LengthCommit { get; }
    public byte[] Ciphertext { get; }
    public byte[] Tag { get; }

    public Lrnc1024Encrypted(string algorithm, int version, int layers, byte[] nonce, byte[] aadMac,
        byte[] lengthSalt, byte[] lengthCommit, byte[] ciphertext, byte[] tag)
    {
        if (algorithm != "LRNC-1024-AEAD") throw new ParameterError("算法标识错误");
        if (version != LrncConstants.Version) throw new ParameterError("版本错误");
        if (layers < 1 || layers > 255) throw new ParameterError("层数错误");
        if (nonce.Length != LrncConstants.NonceBytes) throw new ParameterError("nonce 必须为 12 字节");
        if (aadMac.Length != LrncConstants.AadMacBytes) throw new ParameterError("AAD MAC 必须为 64 字节");
        if (lengthSalt.Length != LrncConstants.LengthSaltBytes) throw new ParameterError("长度承诺盐必须为 32 字节");
        if (lengthCommit.Length != LrncConstants.LengthCommitBytes) throw new ParameterError("长度承诺必须为 64 字节");
        if (tag.Length != LrncConstants.TagBytes) throw new ParameterError("认证标签必须为 32 字节");
        if ((long)ciphertext.Length > (LrncConstants.MaxMessageBytes + LrncConstants.BlockBytes) * 2)
            throw new ParameterError("密文长度超过实现上限");
        Algorithm = algorithm; Version = version; Layers = layers;
        Nonce = nonce; AadMac = aadMac; LengthSalt = lengthSalt;
        LengthCommit = lengthCommit; Ciphertext = ciphertext; Tag = tag;
    }

    public byte[] Encode()
    {
        var header = LrncPrimitives.Concat(
            "LRNC"u8.ToArray(),
            new[] { (byte)Version, (byte)Layers, (byte)LrncConstants.NonceBytes, (byte)LrncConstants.TagBytes },
            LrncPrimitives.U16Le(LrncConstants.AadMacBytes),
            LrncPrimitives.U16Le(LrncConstants.LengthSaltBytes),
            LrncPrimitives.U16Le(LrncConstants.LengthCommitBytes),
            Nonce);
        return LrncPrimitives.Concat(header, AadMac, LengthSalt, LengthCommit, Tag, Ciphertext);
    }

    public static Lrnc1024Encrypted Decode(byte[] data)
    {
        int minLen = 4 + 2 + 2 + 2 + 2 + LrncConstants.NonceBytes + LrncConstants.AadMacBytes
            + LrncConstants.LengthSaltBytes + LrncConstants.LengthCommitBytes + LrncConstants.TagBytes;
        if (data.Length < minLen) throw new ParameterError("封装格式过短");
        if (data[0] != 'L' || data[1] != 'R' || data[2] != 'N' || data[3] != 'C') throw new ParameterError("封装格式标识错误");
        int version = data[4];
        int layers = data[5];
        int nonceLength = data[6];
        int tagLength = data[7];
        int aadMacLength = data[8] | (data[9] << 8);
        int saltLength = data[10] | (data[11] << 8);
        int commitLength = data[12] | (data[13] << 8);
        if (version != LrncConstants.Version || nonceLength != LrncConstants.NonceBytes || tagLength != LrncConstants.TagBytes)
            throw new ParameterError("封装版本、nonce 或 tag 参数不兼容");
        if (aadMacLength != LrncConstants.AadMacBytes) throw new ParameterError("不支持的 AAD MAC 长度");
        if (saltLength != LrncConstants.LengthSaltBytes) throw new ParameterError("不支持的长度承诺盐长度");
        if (commitLength != LrncConstants.LengthCommitBytes) throw new ParameterError("不支持的长度承诺长度");

        int offset = 14;
        var nonce = new byte[LrncConstants.NonceBytes];
        Array.Copy(data, offset, nonce, 0, LrncConstants.NonceBytes);
        offset += LrncConstants.NonceBytes;
        var aadMac = new byte[LrncConstants.AadMacBytes];
        Array.Copy(data, offset, aadMac, 0, LrncConstants.AadMacBytes);
        offset += LrncConstants.AadMacBytes;
        var salt = new byte[LrncConstants.LengthSaltBytes];
        Array.Copy(data, offset, salt, 0, LrncConstants.LengthSaltBytes);
        offset += LrncConstants.LengthSaltBytes;
        var commit = new byte[LrncConstants.LengthCommitBytes];
        Array.Copy(data, offset, commit, 0, LrncConstants.LengthCommitBytes);
        offset += LrncConstants.LengthCommitBytes;
        var tag = new byte[LrncConstants.TagBytes];
        Array.Copy(data, offset, tag, 0, LrncConstants.TagBytes);
        offset += LrncConstants.TagBytes;
        var ciphertext = new byte[data.Length - offset];
        Array.Copy(data, offset, ciphertext, 0, ciphertext.Length);

        return new Lrnc1024Encrypted("LRNC-1024-AEAD", version, layers, nonce, aadMac, salt, commit, ciphertext, tag);
    }
}

public sealed class Lrnc1024Aead
{
    private readonly Lrnc1024KeySet _keySet;
    private readonly HashSet<string> _usedNonces;
    private readonly bool _useNonceTracker;

    public Lrnc1024Aead(Lrnc1024KeySet keySet, bool useNonceTracker = true)
    {
        _keySet = keySet;
        _usedNonces = new HashSet<string>(StringComparer.Ordinal);
        _useNonceTracker = useNonceTracker;
    }

    private void CheckAndRecordNonce(byte[] nonce)
    {
        if (nonce.Length != LrncConstants.NonceBytes) throw new ParameterError("nonce 必须为 12 字节");
        if (_useNonceTracker)
        {
            string key = Convert.ToHexString(nonce);
            if (!_usedNonces.Add(key)) throw new ParameterError("检测到重复 nonce：拒绝加密");
        }
    }

    public Lrnc1024Encrypted Encrypt(byte[] message, byte[] nonce, byte[]? aad = null)
    {
        aad ??= Array.Empty<byte>();
        CheckAndRecordNonce(nonce);

        byte[] padded = LrncPrimitives.Pad(message);
        byte[][] ptBlocks = LrncPrimitives.Absorb(padded);
        var aadCommitments = new List<byte[]>();

        byte[] aadMac = LrncPrimitives.AadMac(_keySet.LayerKeys[0], nonce, aad);
        ulong[] state = LrncPrimitives.Le64Unpack(LrncPrimitives.Absorb(aadMac)[0]);

        var ctBlocks = new List<byte[]>();
        for (int bi = 0; bi < ptBlocks.Length; bi++)
        {
            ulong[] layerState = (ulong[])state.Clone();
            byte[] biLe = LrncPrimitives.U64Le((ulong)bi);
            for (int layer = 0; layer < _keySet.Layers; layer++)
            {
                var (lk, comm) = _keySet.GetLayer(layer);
                aadCommitments.Add(comm);
                byte[] material = LrncPrimitives.Kdf(lk, LrncPrimitives.Concat(LrncConstants.BlockDomain, biLe, nonce), 0, 128);
                ulong[] matWords = LrncPrimitives.Le64Unpack(material);
                layerState = LrncPrimitives.LayerEncrypt(layerState, lk, layer);
                for (int i = 0; i < LrncConstants.StateWords; i++) layerState[i] ^= matWords[i];
                for (int i = 0; i < LrncConstants.StateWords; i++) layerState[i] ^= comm[i % comm.Length];
            }
            byte[] keystream = LrncPrimitives.Le64Pack(layerState);
            byte[] ctb = LrncPrimitives.Xor(ptBlocks[bi], keystream);
            ctBlocks.Add(ctb);
            state = LrncPrimitives.Le64Unpack(ctb); // 标准 CFB：下一块输入 = 密文块
        }

        byte[] ciphertext = LrncPrimitives.Join(ctBlocks.ToArray());
        byte[] authKey = LrncPrimitives.AuthKey(_keySet.LayerKeys[^1], _keySet.Commitments[^1], nonce);
        var (salt, commit) = LrncPrimitives.CreateLengthBinding(_keySet.LayerKeys[^1], authKey, message.Length);
        byte[] authBase = LrncPrimitives.Concat(LrncPrimitives.Join(aadCommitments.ToArray()), nonce, aadMac, salt, commit, ciphertext);
        byte[] tag = LrncPrimitives.HmacSha512First(authKey, authBase, LrncConstants.TagBytes);

        return new Lrnc1024Encrypted("LRNC-1024-AEAD", LrncConstants.Version, _keySet.Layers,
            nonce, aadMac, salt, commit, ciphertext, tag);
    }

    public byte[] Decrypt(Lrnc1024Encrypted packet, byte[]? aad = null)
    {
        aad ??= Array.Empty<byte>();
        if (packet.Layers != _keySet.Layers) throw new AuthenticationError("层数不匹配");
        if (packet.Ciphertext.Length % LrncConstants.BlockBytes != 0)
            throw new AuthenticationError("密文长度不是状态块大小的整数倍");

        byte[] expectedAadMac = LrncPrimitives.AadMac(_keySet.LayerKeys[0], packet.Nonce, aad);
        if (!LrncPrimitives.ConstantTimeEqual(expectedAadMac, packet.AadMac))
            throw new AuthenticationError("AAD 或 nonce 验证失败");

        byte[][] ctBlocks = LrncPrimitives.Split(packet.Ciphertext, LrncConstants.BlockBytes);
        var aadCommitments = new List<byte[]>();
        ulong[] state = LrncPrimitives.Le64Unpack(LrncPrimitives.Absorb(expectedAadMac)[0]);
        var recovered = new MemoryStream();

        for (int bi = 0; bi < ctBlocks.Length; bi++)
        {
            ulong[] layerState = (ulong[])state.Clone();
            byte[] biLe = LrncPrimitives.U64Le((ulong)bi);
            for (int layer = 0; layer < _keySet.Layers; layer++)
            {
                var (lk, comm) = _keySet.GetLayer(layer);
                aadCommitments.Add(comm);
                byte[] material = LrncPrimitives.Kdf(lk, LrncPrimitives.Concat(LrncConstants.BlockDomain, biLe, packet.Nonce), 0, 128);
                ulong[] matWords = LrncPrimitives.Le64Unpack(material);
                layerState = LrncPrimitives.LayerEncrypt(layerState, lk, layer);
                for (int i = 0; i < LrncConstants.StateWords; i++) layerState[i] ^= matWords[i];
                for (int i = 0; i < LrncConstants.StateWords; i++) layerState[i] ^= comm[i % comm.Length];
            }
            byte[] keystream = LrncPrimitives.Le64Pack(layerState);
            byte[] ptb = LrncPrimitives.Xor(ctBlocks[bi], keystream);
            recovered.Write(ptb);
            state = LrncPrimitives.Le64Unpack(ctBlocks[bi]);
        }

        byte[] authKey = LrncPrimitives.AuthKey(_keySet.LayerKeys[^1], _keySet.Commitments[^1], packet.Nonce);
        byte[] authBase = LrncPrimitives.Concat(LrncPrimitives.Join(aadCommitments.ToArray()), packet.Nonce,
            expectedAadMac, packet.LengthSalt, packet.LengthCommit, packet.Ciphertext);
        byte[] expectedTag = LrncPrimitives.HmacSha512First(authKey, authBase, LrncConstants.TagBytes);
        if (!LrncPrimitives.ConstantTimeEqual(expectedTag, packet.Tag))
            throw new AuthenticationError("认证标签验证失败：拒绝释放明文");

        byte[] padded = recovered.ToArray();
        var (message, _) = LrncPrimitives.Unpad(padded);
        LrncPrimitives.VerifyLengthBinding(packet.LengthCommit, packet.LengthSalt, message, _keySet.LayerKeys[^1]);
        return message;
    }
}

/// <summary>
/// 面向应用的高层会话：口令 → PBKDF2 → 主密钥 → KeySet；
/// 加密自动生成随机 nonce 并维护已用目录；解密从封装读取 nonce。
/// </summary>
public sealed class Lrnc1024Session
{
    private readonly Lrnc1024Aead _aead;
    private readonly HashSet<string> _usedNonces = new(StringComparer.Ordinal);

    public Lrnc1024Session(byte[] masterKey)
    {
        if (masterKey.Length != LrncConstants.KeyBytes) throw new ParameterError("主密钥必须为 32 字节");
        var keySet = Lrnc1024KeySet.FromMasterSecret(masterKey);
        _aead = new Lrnc1024Aead(keySet, useNonceTracker: false);
    }

    /// <summary>PBKDF2-SHA512(100000 轮, 16 字节盐) 派生 32 字节主密钥。</summary>
    public static byte[] DeriveMasterKey(string password, byte[] salt)
    {
        if (string.IsNullOrEmpty(password)) throw new ParameterError("口令不能为空");
        if (salt.Length != 16) throw new ParameterError("PBKDF2 盐必须为 16 字节");
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA512, LrncConstants.KeyBytes);
    }

    public byte[] Encrypt(string message) => Encrypt(System.Text.Encoding.UTF8.GetBytes(message), Array.Empty<byte>());

    public byte[] Encrypt(byte[] message, byte[]? aad = null)
    {
        aad ??= Array.Empty<byte>();
        byte[] nonce = RandomNumberGenerator.GetBytes(LrncConstants.NonceBytes);
        string nonceKey = Convert.ToHexString(nonce);
        while (!_usedNonces.Add(nonceKey))
        {
            nonce = RandomNumberGenerator.GetBytes(LrncConstants.NonceBytes);
            nonceKey = Convert.ToHexString(nonce);
        }
        var pkt = _aead.Encrypt(message, nonce, aad);
        return pkt.Encode();
    }

    public byte[] Decrypt(byte[] packet, byte[]? aad = null)
    {
        aad ??= Array.Empty<byte>();
        var pkt = Lrnc1024Encrypted.Decode(packet);
        return _aead.Decrypt(pkt, aad);
    }

    public string DecryptString(byte[] packet)
    {
        var plain = Decrypt(packet);
        return System.Text.Encoding.UTF8.GetString(plain);
    }
}
