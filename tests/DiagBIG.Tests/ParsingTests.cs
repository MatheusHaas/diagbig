using System;
using System.IO;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    internal static class Samples
    {
        public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "amostras", name);
        public static string Text(string name) => File.ReadAllText(Path(name));
    }

    public class WinPathTests
    {
        [Theory]
        [InlineData("\"C:/BIG_db/data/\"", @"C:\BIG_db\data\")]
        [InlineData(@"C:\\Program Files\\MariaDB 10.4\\data", @"C:\Program Files\MariaDB 10.4\data")]
        [InlineData(@"\\servidor\share\x", @"\\servidor\share\x")]
        [InlineData("D:", @"D:\")]
        public void Normalize(string input, string expected) => Assert.Equal(expected, WinPath.Normalize(input));

        [Fact]
        public void CombineAndRooted()
        {
            Assert.Equal(@"C:\BIG_db\data\x.err", WinPath.Combine(@"C:\BIG_db\data\", @".\x.err"));
            Assert.Equal(@"D:\y.err", WinPath.Combine(@"C:\BIG_db\data", @"D:\y.err"));
            Assert.True(WinPath.IsRooted(@"\\srv\a"));
            Assert.False(WinPath.IsRooted(@"logs\a.err"));
            Assert.Equal(@"C:\", WinPath.GetDirectoryName(@"C:\BIG_db"));
            Assert.True(WinPath.HasExtension(@"C:\a.b\host.err"));
            Assert.False(WinPath.HasExtension(@"C:\a.b\erros_bigdb"));
        }
    }

    public class ImagePathParserTests
    {
        [Fact]
        public void QuotedExeAndQuotedDefaultsValue()
        {
            var i = ImagePathParser.Parse("\"C:\\BIG_db\\bin\\mysqld.exe\" --defaults-file=\"C:\\BIG_db\\my.ini\" BIG_db");
            Assert.Equal(@"C:\BIG_db\bin\mysqld.exe", i.ExePath);
            Assert.Equal(@"C:\BIG_db\my.ini", i.DefaultsFile);
        }

        [Fact]
        public void UnquotedExeWithSpacesAndQuotedWholeArgument()
        {
            var i = ImagePathParser.Parse("C:\\Program Files\\MariaDB 10.4\\bin\\mysqld.exe \"--defaults-file=C:\\Program Files\\MariaDB 10.4\\data\\my.ini\" MariaDB");
            Assert.Equal(@"C:\Program Files\MariaDB 10.4\bin\mysqld.exe", i.ExePath);
            Assert.Equal(@"C:\Program Files\MariaDB 10.4\data\my.ini", i.DefaultsFile);
        }

        [Fact]
        public void UnquotedDefaultsWithForwardSlashes()
        {
            var i = ImagePathParser.Parse("\"C:/MeuBanco/bin/mariadbd.exe\" --defaults-file=C:/MeuBanco/my.ini MeuBanco");
            Assert.Equal(@"C:\MeuBanco\bin\mariadbd.exe", i.ExePath);
            Assert.Equal(@"C:\MeuBanco\my.ini", i.DefaultsFile);
        }

        [Fact]
        public void NoDefaultsFile()
        {
            var i = ImagePathParser.Parse("\"C:\\BIG_db\\bin\\mysqld\" BIG_db");
            Assert.Null(i.DefaultsFile);
            Assert.Equal(@"C:\BIG_db\bin\mysqld", i.ExePath);
        }

        [Theory]
        [InlineData("\"C:\\BIG_db\\bin\\mysqld.exe\" BIG_db", true)]
        [InlineData("\"C:\\BIG_db\\bin\\MYSQLD\" BIG_db", true)]
        [InlineData("C:\\Program Files\\MariaDB 11.4\\bin\\mariadbd.exe --defaults-file=x MariaDB", true)]
        [InlineData("C:\\mysql50\\bin\\mysqld-nt.exe MySQL", true)]
        [InlineData("C:\\Windows\\system32\\svchost.exe -k netsvcs", false)]
        [InlineData("C:\\x\\mysqldump.exe", false)]
        [InlineData("C:\\Program Files\\MySQL\\MySQL Router\\bin\\mysqlrouter.exe", false)]
        [InlineData("", false)]
        public void IsDatabase(string imagePath, bool expected) => Assert.Equal(expected, ImagePathParser.IsDatabaseImagePath(imagePath));
    }

    public class MyIniTests
    {
        [Fact]
        public void BigDbForwardSlashesRelativeLogErrorWithoutExtension()
        {
            var o = MyIniParser.Parse(Samples.Text("my_bigdb_barra_normal.ini"));
            Assert.Equal(@"C:\BIG_db\data\", o.DataDir);
            Assert.Equal("erros_bigdb", o.LogError);
            var p = DbPathResolver.Resolve(o, @"C:\BIG_db", "CAIXA01");
            Assert.Equal(@"C:\BIG_db\data", p.DataDir);
            Assert.Equal(@"C:\BIG_db\data\erros_bigdb.err", p.ErrPath);
        }

        [Fact]
        public void WithoutLogErrorUsesComputerName_AndIgnoresOtherSections()
        {
            var o = MyIniParser.Parse(Samples.Text("my_sem_log_error.ini"));
            Assert.False(o.LogErrorDefined);
            var p = DbPathResolver.Resolve(o, @"C:\MeuBanco", "CAIXA01");
            Assert.Equal(@"C:\MeuBanco\data\CAIXA01.err", p.ErrPath);
            Assert.Contains("não definido", p.ErrRule);
        }

        [Fact]
        public void MariaDbVersionedSectionAbsoluteEscapedPath()
        {
            var o = MyIniParser.Parse(Samples.Text("my_mariadb104_absoluto.ini"));
            Assert.Equal(@"C:\Program Files\MariaDB 10.4\data", o.DataDir);
            var p = DbPathResolver.Resolve(o, @"C:\Program Files\MariaDB 10.4", "PC");
            Assert.Equal(@"D:\Logs\MariaDB\mariadb.err", p.ErrPath);
        }

        [Fact]
        public void Mysql55RelativeSubfolder_NotConfusedWithVerbosity()
        {
            var o = MyIniParser.Parse(Samples.Text("my_mysql55_relativo.ini"));
            var p = DbPathResolver.Resolve(o, @"C:\Program Files\MySQL\MySQL Server 5.5", "PC");
            Assert.Equal(@"C:\ProgramData\MySQL\MySQL Server 5.5\Data\logs\servidor.err", p.ErrPath);
        }

        [Fact]
        public void LogErrorWithoutValueAndUnderscoreVariant()
        {
            var o = MyIniParser.Parse("[mysqld]\r\nlog-error\r\n");
            Assert.True(o.LogErrorDefined);
            Assert.Equal(@"C:\db\data\PC.err", DbPathResolver.Resolve(o, @"C:\db", "PC").ErrPath);

            o = MyIniParser.Parse("[mariadbd]\nloose_log_error = 'C:/x/y.log'\n[mysqld8.0]\ndatadir=dados\n");
            var p = DbPathResolver.Resolve(o, @"C:\db", "PC");
            Assert.Equal(@"C:\x\y.log", p.ErrPath);
            Assert.Equal(@"C:\db\dados", p.DataDir);
        }

        [Fact]
        public void NoIniAtAll()
        {
            var p = DbPathResolver.Resolve(null, DbPathResolver.BaseDirFromExe(@"C:\BIG_db\bin\mysqld.exe"), "LOJA");
            Assert.Equal(@"C:\BIG_db\data", p.DataDir);
            Assert.Equal(@"C:\BIG_db\data\LOJA.err", p.ErrPath);
            Assert.Equal(new[] { @"C:\BIG_db\my.ini", @"C:\BIG_db\my.cnf", @"C:\BIG_db\data\my.ini", @"C:\BIG_db\data\my.cnf" },
                         DbPathResolver.IniCandidates(@"C:\BIG_db"));
        }
    }
}
