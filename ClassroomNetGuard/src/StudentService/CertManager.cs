using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace StudentService
{
    /// <summary>
    /// HTTPS 管控证书管理：生成并信任根 CA，为被拦截的域名动态签发站点证书（MITM）。
    /// 站点证书按域名缓存。CA 与私钥保存在 %ProgramData%\NetGuard\cert\ca.pfx。
    /// 说明：CA 装入本机信任根后，学生机可被本系统解密查看 HTTPS 流量，仅用于课堂管控。
    /// </summary>
    public sealed class CertManager
    {
        const string CaPfxPassword = "NetGuardCA!2026";
        static readonly string _certDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetGuard", "cert");
        static readonly string _caPfx = Path.Combine(_certDir, "ca.pfx");

        readonly ConcurrentDictionary<string, X509Certificate2> _cache = new ConcurrentDictionary<string, X509Certificate2>();
        readonly object _sync = new object();
        AsymmetricCipherKeyPair _caKey;
        Org.BouncyCastle.X509.X509Certificate _caCert;

        /// <summary>确保 CA 存在并已装入本机信任根（服务以 LocalSystem 运行，有权限）。</summary>
        public void EnsureCa()
        {
            lock (_sync)
            {
                LoadOrCreateCa();
                InstallToRootStore();
            }
        }

        void LoadOrCreateCa()
        {
            if (File.Exists(_caPfx))
            {
                try
                {
                    var pfx = new X509Certificate2(_caPfx, CaPfxPassword, X509KeyStorageFlags.Exportable);
                    _caCert = new X509CertificateParser().ReadCertificate(pfx.Export(X509ContentType.Cert));
                    _caKey = DotNetUtilities.GetKeyPair(pfx.PrivateKey);
                    return;
                }
                catch { }
            }

            Directory.CreateDirectory(_certDir);
            var gen = new RsaKeyPairGenerator();
            gen.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
            var kp = gen.GenerateKeyPair();

            var cn = new X509Name("CN=NetGuard Classroom CA");
            var cg = new X509V3CertificateGenerator();
            cg.SetSerialNumber(new BigInteger(1, Guid.NewGuid().ToByteArray()));
            cg.SetIssuerDN(cn);
            cg.SetSubjectDN(cn);
            cg.SetNotBefore(DateTime.UtcNow.AddDays(-1));
            cg.SetNotAfter(DateTime.UtcNow.AddYears(10));
            cg.SetPublicKey(kp.Public);
            cg.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
            cg.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign | KeyUsage.DigitalSignature));
            var ca = cg.Generate(new Asn1SignatureFactory("SHA256WithRSA", kp.Private));

            var store = new Pkcs12StoreBuilder().Build();
            store.SetKeyEntry("ca", new AsymmetricKeyEntry(kp.Private), new[] { new X509CertificateEntry(ca) });
            using (var ms = new MemoryStream())
            {
                store.Save(ms, CaPfxPassword.ToCharArray(), new SecureRandom());
                File.WriteAllBytes(_caPfx, ms.ToArray());
            }
            _caCert = ca;
            _caKey = kp;
        }

        void InstallToRootStore()
        {
            var dotNet = new X509Certificate2(_caCert.GetEncoded());
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            var exists = false;
            foreach (var c in store.Certificates)
            {
                if (string.Equals(c.Thumbprint, dotNet.Thumbprint, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
            }
            if (!exists) store.Add(dotNet);
            store.Close();
        }

        /// <summary>获取（或签发并缓存）某域名的服务器证书，供代理 TLS 握手使用。</summary>
        public X509Certificate2 GetSiteCertificate(string host)
        {
            if (_cache.TryGetValue(host, out var c)) return c;
            lock (_sync)
            {
                if (_cache.TryGetValue(host, out c)) return c;
                var site = IssueSiteCertificate(host);
                _cache[host] = site;
                return site;
            }
        }

        X509Certificate2 IssueSiteCertificate(string host)
        {
            var gen = new RsaKeyPairGenerator();
            gen.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
            var kp = gen.GenerateKeyPair();

            var cg = new X509V3CertificateGenerator();
            cg.SetSerialNumber(new BigInteger(1, Guid.NewGuid().ToByteArray()));
            cg.SetIssuerDN(_caCert.SubjectDN);
            cg.SetSubjectDN(new X509Name("CN=" + host));
            cg.SetNotBefore(DateTime.UtcNow.AddDays(-1));
            cg.SetNotAfter(DateTime.UtcNow.AddYears(1));
            cg.SetPublicKey(kp.Public);
            cg.AddExtension(X509Extensions.SubjectAlternativeName, false,
                new GeneralNames(new GeneralName(GeneralName.DnsName, host)));
            cg.AddExtension(X509Extensions.BasicConstraints, false, new BasicConstraints(false));
            cg.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.KeyEncipherment));
            cg.AddExtension(X509Extensions.ExtendedKeyUsage, true, new ExtendedKeyUsage(KeyPurposeID.id_kp_serverAuth));
            var cert = cg.Generate(new Asn1SignatureFactory("SHA256WithRSA", _caKey.Private));

            var store = new Pkcs12StoreBuilder().Build();
            store.SetKeyEntry("site", new AsymmetricKeyEntry(kp.Private), new[] { new X509CertificateEntry(cert) });
            using var ms = new MemoryStream();
            store.Save(ms, CaPfxPassword.ToCharArray(), new SecureRandom());
            return new X509Certificate2(ms.ToArray(), CaPfxPassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }
    }
}
