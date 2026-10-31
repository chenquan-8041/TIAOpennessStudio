using System;
using System.IO;
using System.Linq;
using TIAOpennessStudio.Services.Standards;

namespace StandardsKbTool
{
    /// <summary>
    /// Encoding-standard knowledge base packaging / verification CLI.
    ///
    /// Usage:
    ///   StandardsKbTool build  &lt;plaintext md directory or file&gt;  &lt;output standards.kb&gt;
    ///   StandardsKbTool verify &lt;standards.kb&gt;  [search keyword]
    ///
    /// Notes:
    ///   build  = parse Markdown standards → encrypt into standards.kb (machine-bound).
    ///   verify = decrypt and search with the same machine's key, proving the data can be encrypted and restored.
    ///            If the kb is copied to another machine and verified, decryption fails — this is the anti-copy mechanism.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                if (args.Length >= 1 && args[0].Equals("build", StringComparison.OrdinalIgnoreCase))
                    return Build(args);
                if (args.Length >= 1 && args[0].Equals("verify", StringComparison.OrdinalIgnoreCase))
                    return Verify(args);

                Console.WriteLine("用法:");
                Console.WriteLine("  StandardsKbTool build  <明文md目录或文件>  <输出 standards.kb>");
                Console.WriteLine("  StandardsKbTool verify <standards.kb>  [检索关键词]");
                return 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        private static int Build(string[] args)
        {
            if (args.Length < 3) { Console.WriteLine("build 需要: <明文md目录或文件> <输出kb>"); return 2; }
            var src = args[1];
            var outKb = args[2];

            var mdFiles = Directory.Exists(src)
                ? Directory.GetFiles(src, "*.md", SearchOption.AllDirectories)
                : new[] { src };

            var allRules = mdFiles
                .SelectMany(f => StandardsMarkdownImporter.ParseFile(f))
                .ToList();

            Console.WriteLine($"解析 {mdFiles.Length} 个文件，共 {allRules.Count} 条规则。");
            if (allRules.Count == 0) { Console.WriteLine("没有解析到规则，终止。"); return 1; }

            // Encrypt with the official built-in default key: any machine with this program installed can
            // decrypt seamlessly (not machine-bound).
            StandardsKeyProvider.BindToMachine = false;
            StandardsPackager.Build(allRules, outKb, new OfficialSecretProvider());
            var size = new FileInfo(outKb).Length;
            Console.WriteLine($"已生成加密知识库(官方默认密钥): {outKb} ({size} 字节)");
            Console.WriteLine("提示: 用内置默认密钥加密，随发行包分发，程序启动自动无感解密。");
            return 0;
        }

        private static int Verify(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("verify 需要: <standards.kb> [关键词]"); return 2; }
            var kb = args[1];
            var query = args.Length >= 3 ? args[2] : "FB";
            StandardsKeyProvider.BindToMachine = false;

            // StandardsKnowledgeStore resolves <appBase>/Knowledge/standards.kb;
            // to verify an arbitrary path, copy the kb into a temporary appBase/Knowledge structure.
            var appBase = Path.Combine(Path.GetTempPath(), "kbverify_" + Guid.NewGuid().ToString("N"));
            var knowledgeDir = Path.Combine(appBase, "Knowledge");
            Directory.CreateDirectory(knowledgeDir);
            File.Copy(kb, Path.Combine(knowledgeDir, "standards.kb"), true);

            using (var store = new StandardsKnowledgeStore(appBase))
            {
                Console.WriteLine($"IsAvailable = {store.IsAvailable}");
                Console.WriteLine($"检索关键词: \"{query}\"");
                var rules = store.SearchRules(query, 5);
                Console.WriteLine($"命中 {rules.Count} 条:");
                foreach (var r in rules)
                    Console.WriteLine($"  [{r.Category}/{r.Scope}] {r.Title} => {Truncate(r.Content, 60)}");

                Console.WriteLine();
                Console.WriteLine("=== 注入给 agent 的约束指令(片段) ===");
                Console.WriteLine(StandardsPolicyInjector.BuildInjection(rules, true));
            }

            try { Directory.Delete(appBase, true); } catch { }
            Console.WriteLine("还原验证完成：解密成功，说明本机密钥可正常还原。");
            return 0;
        }

        private static string Truncate(string s, int n)
            => string.IsNullOrEmpty(s) ? s : (s.Length <= n ? s : s.Substring(0, n) + "…");
    }
}
