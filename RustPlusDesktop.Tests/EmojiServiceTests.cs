using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustPlusDesk.Services.Emoji;

namespace RustPlusDesktop.Tests;

[TestClass]
public class EmojiServiceTests
{
    [TestMethod]
    public void CustomEmojis_Contains28Emojis()
    {
        Assert.AreEqual(28, EmojiService.CustomEmojis.Count);
    }

    [TestMethod]
    [DataRow("happy", ":happy:")]
    [DataRow("angry", ":angry:")]
    [DataRow("heart", ":heart:")]
    [DataRow("coffeecan", ":coffeecan:")]
    [DataRow("yellowpin", ":yellowpin:")]
    public void ResolveEmojiOrItem_MatchesCustomEmojis(string name, string expectedTag)
    {
        var entry = EmojiService.ResolveEmojiOrItem(name);
        Assert.IsNotNull(entry);
        Assert.AreEqual(expectedTag, entry.Tag);
        Assert.IsTrue(entry.IsCustomEmoji);
    }

    [TestMethod]
    public void Search_FindsMatchingCustomEmojis()
    {
        var results = EmojiService.Search("hap").ToList();
        Assert.IsTrue(results.Any(r => r.Name == "happy"));
    }

    [TestMethod]
    public void EmojiTokenRegex_ExtractsAllTokensInMessage()
    {
        var text = "Hey team :happy: let's get :metal.plate.torso: and :rifle.ak: ready!";
        var matches = EmojiService.EmojiTokenRegex.Matches(text);

        Assert.AreEqual(3, matches.Count);
        Assert.AreEqual("happy", matches[0].Groups["token"].Value);
        Assert.AreEqual("metal.plate.torso", matches[1].Groups["token"].Value);
        Assert.AreEqual("rifle.ak", matches[2].Groups["token"].Value);
    }

    [TestMethod]
    public void GetCustomEmojiFrames_LoadsAllEmojis()
    {
        foreach (var emoji in EmojiService.CustomEmojis)
        {
            var frames = EmojiService.GetCustomEmojiFrames(emoji.Name);
            Assert.IsNotNull(frames, $"Frames for {emoji.Name} should not be null");
            Assert.IsTrue(frames.Length > 0, $"Frames for {emoji.Name} should be > 0");
            var delays = EmojiService.GetFrameDelays(emoji.Name);
            Assert.IsNotNull(delays);
            Assert.AreEqual(frames.Length, delays.Length);
            Assert.IsTrue(delays.All(delay => delay > 0), $"Frame timing for {emoji.Name} must be preserved");
            Assert.IsTrue(frames.All(frame => frame.IsFrozen), "Background decoded frames must be safe to use on the UI thread");
            // Read WebP ANMF durations independently of the decoder to catch lost frames/timing.
            var data = File.ReadAllBytes(EmojiService.GetCustomEmojiWebpPath(emoji.Name)!);
            var authoredDurations = new List<int>();
            for (int offset = 12; offset + 8 <= data.Length;)
            {
                int length = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4)));
                if (System.Text.Encoding.ASCII.GetString(data, offset, 4) == "ANMF")
                {
                    int duration = data[offset + 20] | data[offset + 21] << 8 | data[offset + 22] << 16;
                    authoredDurations.Add(duration > 0 ? duration : 16);
                }
                offset = checked(offset + 8 + length + (length & 1));
            }
            if (authoredDurations.Count > 0)
                CollectionAssert.AreEqual(authoredDurations.ToArray(), delays, $"Authored WebP timing for {emoji.Name}");
        }
    }
}
