using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using RomM.Saves;
using Xunit;

namespace RomM.Tests
{
    public class Ps2MemoryCardTests
    {
        // A 1 MB card with ECC made by mymcplus: BASLUS-21240POW holding file0..2.bin (1500, 3000
        // and 4500 bytes: chains of several clusters), and BASLUS-20000OTHER holding icon.sys.
        private const string CardGz =
            "H4sIAK4f5moC/+3dd3RV9QHA8UsGFwgYohbiqI0ZIiovLyG1WmyrEhURTCBGEbA1QohBQ1LQPCOapNa6997aulr3LtpdZ/dwt1Zt" +
            "6XC11e72H3pvREOiOSWcHvF5P9/Dj6y/fvd+zo93fvf3SEP7sq6S+obqktnNbe3Lu0qmNy1fXLJ3+/K2pqNKqlLVqXQqHfSVkxOM" +
            "D4I1QV4QjIu+XJ375rd7otEd9Dcq2Lit2cjl7BhkVaWdnZmengn1ldHfgRJfxODtP66GeBAP4kE8iAfxIB7Eg3gQD+JBPIgHZZOH" +
            "0dEYE42CaIwNNv75Ab23zR2VGeDBFUl2g9eHnCDozY1G/LNorejNX/t5GH0ctfbzuGj96I3Wj95x0dhk7ffHr/PzqB4j+0Zm1qzB" +
            "64PrkmQPg9YH14QHHgweDB4MHgwePjBj4+0/hIUdsYdJJwbxW66CIJWXM6LglfDtjczBXw9RypZvVleTt3LA+rDddUPc//X1AERW" +
            "V9TdPcBDvD4Mvv851ofEFG5+xP/0YH1ITrWDXk/GHvLXuf8jhulhzz0aZjU2TKmuqq5J19cd5AJnWaVlZe/wkLvO/S/cQA/pqLoD" +
            "Zuw11yXOqqY2Nw/wMDHy8Fx+//3PG6aHJa1HNqdTh7Uuc2mz8zzMu3hYVdB//0dugIcqHrLXQzo98LzciJzcvPyR4ajRYwrGjtuk" +
            "cHzRpptt/qEJE4u32HKrrT+8zUdKti0tK6/YbtL2k3fYcacpqcp0VfXUmo/u/LFddv34tN0+8clP7b7HntNr99p7nxn7ztxv1uz9" +
            "6+rnzG04oPHAg+YdPH/BwkM+/ZlDmw5btLh5ScvhrUuPOLJtWXvHZ5evOOrozswxXceuPO747p7ez53w+RO/cNLJp5x62ulnnHnW" +
            "2eece975F1x40cWXXHrZ5VdcedXV13zxS9ded/0NN375KzfdfMutt91+x5133X3Pvfd9ddX9D3zt69/45re+/Z3vPvjQw488+tj3" +
            "vv+DH/7oxz/56c9+/vgTTz719DPP/uKXz/3q+Rde/PVvVv/2d7//w0svv/Lqa3/8059ff+Mvf/3b3//xz3/9+z+mHk09ltDVFU6r" +
            "DaZNizlQkOypZ7q7Gzs7w3Rd6eTJsQcKPrhTX8/ztBUrxoaT5jXaOdA7z1dbN5M99XCXfSZUz44wFLW09O1fU5DoqYeV9UvKy6NX" +
            "EVNbW2MPFCR76mH5gtrK+D+frGjbNPZAQbKnHm61eG5qTrQ+hIdPjD1QkOyph+PbO3beN349uWBB7IGC7Jz6/+15d3hMUFyc6TsF" +
            "8dZ+9QVF/fvTYzZgv7rafnXW1lhV5f3dGnL/wb+yyZ56/Eqyc3S4U0NHui72QEGypx4vC21tYdnCTGXlm+djKEjy1OOdqJkzw+KW" +
            "2vy+Y3IUJHvq0fowvaU4LOyoqZgfe6Ag2VOP1oemRVuHeSsnlPftP1CQ7KnHvyzpkNJwZV7TNk2xBwqSPfVofaiZNynsKJw+siv2" +
            "QEGypx6tD7UFBWFL8dQZM2IPFLz/pv6e7kd1d2emTAkXlhUtXWp3Tpl3eX/W4OcVucN4fuHQXZa3Hh6G8zzLKczsbvD7N+Pnmw/l" +
            "9t//omF6aF3Uviy1omuFK5udhbnHeb6pIV8/1DdU17oqya0i53jrg4ZcH8QDD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8" +
            "iAfxIB54EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAeeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyI" +
            "B/HAg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxwIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH" +
            "HsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIBx7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE88CAe" +
            "xIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPPAgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/igQfxIB7E" +
            "g3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4oEH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQDzyIB/EgHsSD" +
            "eBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA88iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeOBBPIgH8SAexIN4" +
            "EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3jgQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexAMP4kE8iAfxIB7Eg3gQ" +
            "D+JBPIgH8SAexIN4EA/iQTyIB/EgHsQDD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB54EA/iQTyIB/EgHsSDeBAP" +
            "4kE8iAfxIB7Eg3gQD+JBPIgH8SAeeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/HAg3gQD+JBPIgH8SAexIN4EA/i" +
            "QTyIB/EgHsSDeBAP4kE8iAfxwIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgHHsSDeBAP4kE8iAfxIB7Eg3gQD+JB" +
            "PIgH8SAexIN4EA/iQTyIBx7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE88CAexIN4EA/iQTyIB/EgHsSDeBAP4kE8" +
            "iAfxIB7Eg3gQD+JBPPAgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/igQfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyI" +
            "B/EgHsSDeBAP4oEH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQDzyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH" +
            "8SAexIN4EA88iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeOBBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfx" +
            "IB7Eg3jgQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexAMP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/Eg" +
            "HsQDD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB54EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAe" +
            "eBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/HAg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxwIN4" +
            "EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgHHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIBx7Eg3gQ" +
            "D+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE88CAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPPAgHsSDeBAP" +
            "4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/igQfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4oEH8SAexIN4EA/i" +
            "QTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQDzyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA88iAfxIB7Eg3gQD+JB" +
            "PIgH8SAexIN4EA/iQTyIB/EgHsSDeOBBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3jgQTyIB/EgHsSDeBAP4kE8" +
            "iAfxIB7Eg3gQD+JBPIgH8SAexAMP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsQDD+JBPIgH8SAexIN4EA/iQTyI" +
            "B/EgHsSDeBAP4kE8iAfxIB54EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAeeBAP4kE8iAfxIB7Eg3gQD+JBPIgH" +
            "8SAexIN4EA/iQTyIB/HAg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxwIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfx" +
            "IB7Eg3gQD+JBPIgHHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIBx7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/Eg" +
            "HsSDeBAP4kE88CAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPPAgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAe" +
            "xIN4EA/igQfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4oEH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7E" +
            "g3gQDzyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA88iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSD" +
            "eOBBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3jgQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexAMP" +
            "4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsQDD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB54EA/i" +
            "QTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAeeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/HAg3gQD+JB" +
            "PIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxwIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgHHsSDeBAP4kE8" +
            "iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIBx7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE88CAexIN4EA/iQTyI" +
            "B/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPPAgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/igQfxIB7Eg3gQD+JBPIgH" +
            "8SAexIN4EA/iQTyIB/EgHsSDeBAP4oEH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQDzyIB/EgHsSDeBAP4kE8iAfx" +
            "IB7Eg3gQD+JBPIgH8SAexIN4EA88iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeOBBPIgH8SAexIN4EA/iQTyIB/Eg" +
            "HsSDeBAP4kE8iAfxIB7Eg3jgQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexAMP4kE8iAfxIB7Eg3gQD+JBPIgH8SAe" +
            "xIN4EA/iQTyIB/EgHsQDD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB54EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7E" +
            "g3gQD+JBPIgH8SAeeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/HAg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSD" +
            "eBAP4kE8iAfxwIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgHHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4" +
            "EA/iQTyIBx7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE88CAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxIB7Eg3gQ" +
            "D+JBPPAgHsSDeBAP4kE8iAfxIB7Eg3gQD+JBPIgH8SAexIN4EA/igQfxIB7Eg3gQD+JBPIgH8SAexIN4EA/iQTyIB/EgHsSDeBAP" +
            "4oEH8SAexIN4EA/iQTyIB/EgHsSDeBAP4kE8iAfxoCR4WCNJkiRJkiRJkiRJkpTlORHiPIzzUeJBPIgH8SAexIN4EA/iQTyIB/Eg" +
            "HsSDst3DfwFiD346AIAQAA==";

        private static byte[] Card()
        {
            using (var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(CardGz)), CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gz.CopyTo(output);
                return output.ToArray();
            }
        }

        private static byte[] Expected(int k) => Enumerable.Range(0, 1500 * (k + 1)).Select(i => (byte)((k * 7 + i) % 251)).ToArray();

        [Fact]
        public void Reads_every_save_and_file()
        {
            var root = Ps2MemoryCard.Read(Card()).Root();
            Assert.Equal(new[] { "BASLUS-20000OTHER", "BASLUS-21240POW" }, root.Select(e => e.Name).OrderBy(n => n).ToArray());
            var pow = root.Single(e => e.Name == "BASLUS-21240POW");
            Assert.True(pow.IsDirectory);
            for (int k = 0; k < 3; k++)
                Assert.Equal(Expected(k), pow.Children.Single(c => c.Name == $"file{k}.bin").Data);
        }

        [Fact]
        public void Writes_a_folder_card_with_a_formatted_superblock()
        {
            string folder = Path.Combine(Path.GetTempPath(), "romm-mcd-" + Guid.NewGuid().ToString("N"));
            try
            {
                var saves = Ps2MemoryCard.Read(Card()).WriteFolderCard(folder);
                Assert.Equal(2, saves.Count);
                byte[] superblock = File.ReadAllBytes(Path.Combine(folder, "_pcsx2_superblock"));
                Assert.Equal(8192, superblock.Length);
                Assert.Equal(0x6F, superblock[0x16]);   // what PCSX2 checks (FolderMemoryCard::IsFormatted)
                Assert.Equal(Expected(2), File.ReadAllBytes(Path.Combine(folder, "BASLUS-21240POW", "file2.bin")));
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }
    }
}
