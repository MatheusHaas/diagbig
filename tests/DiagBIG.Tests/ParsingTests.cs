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
        [InlineData("\"C:/mysql/data/\"", @"C:\mysql\data\")]
        [InlineData(@"C:\\Program Files\\MariaDB 10.4\\data", @"C:\Program Files\MariaDB 10.4\data")]
        [InlineData(@"\\servidor\share\x", @"\\servidor\share\x")]
        [InlineData("D:", @"D:\")]
        public void Normalize(string input, string expected) => Assert.Equal(expected, WinPath.Normalize(input));

        [Fact]
        public void CombineAndRooted()
        {
            Assert.Equal(@"C:\mysql\data\x.err", WinPath.Combine(@"C:\mysql\data\", @".\x.err"));
            Assert.Equal(@"D:\y.err", WinPath.Combine(@"C:\mysql\data", @"D:\y.err"));
            Assert.True(WinPath.IsRooted(@"\\srv\a"));
            Assert.False(WinPath.IsRooted(@"logs\a.err"));
            Assert.Equal(@"C:\", WinPath.GetDirectoryName(@"C:\mysql"));
            Assert.True(WinPath.HasExtension(@"C:\a.b\host.err"));
            Assert.False(WinPath.HasExtension(@"C:\a.b\erros_mariadb"));
        }
    }

    public class ImagePathParserTests
    {
        [Fact]
        public void QuotedExeAndQuotedDefaultsValue()
        {
            var i = ImagePathParser.Parse("\"C:\\mysql\\bin\\mysqld.exe\" --defaults-file=\"C:\\mysql\\my.ini\" mysql");
            Assert.Equal(@"C:\mysql\bin\mysqld.exe", i.ExePath);
            Assert.Equal(@"C:\mysql\my.ini", i.DefaultsFile);
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
        public void Mysql51ServiceWithoutExeExtension()
        {
            var i = ImagePathParser.Parse("\"C:\\Program Files\\MySQL\\MySQL Server 5.1\\bin\\mysqld\" --defaults-file=\"C:\\Program Files\\MySQL\\MySQL Server 5.1\\my.ini\" MySQL");
            Assert.Equal(@"C:\Program Files\MySQL\MySQL Server 5.1\bin\mysqld", i.ExePath);
            Assert.Equal(@"C:\Program Files\MySQL\MySQL Server 5.1\my.ini", i.DefaultsFile);
            Assert.True(ImagePathParser.IsDatabaseImagePath("\"C:\\Program Files\\MySQL\\MySQL Server 5.1\\bin\\mysqld\" MySQL"));
        }

        [Fact]
        public void NoDefaultsFile()
        {
            var i = ImagePathParser.Parse("\"C:\\mysql\\bin\\mysqld\" mysql");
            Assert.Null(i.DefaultsFile);
            Assert.Equal(@"C:\mysql\bin\mysqld", i.ExePath);
        }

        [Theory]
        [InlineData("\"C:\\mysql\\bin\\mysqld.exe\" mysql", true)]
        [InlineData("\"C:\\mysql\\bin\\MYSQLD\" mysql", true)]
        [InlineData("C:\\Program Files\\MariaDB 11.4\\bin\\mariadbd.exe --defaults-file=x MariaDB", true)]
        [InlineData("C:\\mysql50\\bin\\mysqld-nt.exe MySQL", true)]
        [InlineData("C:\\mysql\\bin\\mysqld-max-nt.exe --defaults-file=C:\\WINDOWS\\my.ini MySQL", true)]
        [InlineData("C:\\mysql\\bin\\mysqld-opt.exe", true)]
        [InlineData("C:\\Windows\\system32\\svchost.exe -k netsvcs", false)]
        [InlineData("C:\\x\\mysqldump.exe", false)]
        [InlineData("C:\\Program Files\\MySQL\\MySQL Router\\bin\\mysqlrouter.exe", false)]
        [InlineData("", false)]
        public void IsDatabase(string imagePath, bool expected) => Assert.Equal(expected, ImagePathParser.IsDatabaseImagePath(imagePath));
    }

    public class MyIniTests
    {
        [Fact]
        public void MariaDbForwardSlashesRelativeLogErrorWithoutExtension()
        {
            var o = MyIniParser.Parse(Samples.Text("my_mariadb_barra_normal.ini"));
            Assert.Equal(@"C:\Program Files\MariaDB 10.4\data\", o.DataDir);
            Assert.Equal("erros_mariadb", o.LogError);
            var p = DbPathResolver.Resolve(o, @"C:\Program Files\MariaDB 10.4", "CAIXA01");
            Assert.Equal(@"C:\Program Files\MariaDB 10.4\data", p.DataDir);
            Assert.Equal(@"C:\Program Files\MariaDB 10.4\data\erros_mariadb.err", p.ErrPath);
            Assert.Empty(p.AlternateErrPaths);
        }

        [Fact]
        public void Mysql4WinMySQLadminIni_DefaultsToComputerNameOrMysqlErr()
        {
            var o = MyIniParser.Parse(Samples.Text("my_mysql4_windows.ini"));
            Assert.Equal(@"C:\mysql\data", o.DataDir);
            Assert.False(o.LogErrorDefined);
            var p = DbPathResolver.Resolve(o, DbPathResolver.BaseDirFromExe(@"C:\mysql\bin\mysqld-nt.exe"), "CAIXA01");
            Assert.Equal(@"C:\mysql\data\CAIXA01.err", p.ErrPath);
            Assert.Equal(new[] { @"C:\mysql\data\mysql.err" }, p.AlternateErrPaths);
        }

        [Fact]
        public void Mysql51InstallerIni_DataInProgramData()
        {
            var o = MyIniParser.Parse(Samples.Text("my_mysql51_instalador.ini"));
            var p = DbPathResolver.Resolve(o, @"C:\Program Files\MySQL\MySQL Server 5.1", "CAIXA01");
            Assert.Equal(@"C:\ProgramData\MySQL\MySQL Server 5.1\Data", p.DataDir);
            Assert.Equal(@"C:\ProgramData\MySQL\MySQL Server 5.1\Data\CAIXA01.err", p.ErrPath);
        }

        [Fact]
        public void MergeFollowsMysqlReadOrder_LastWins()
        {
            var windows = MyIniParser.Parse("[mysqld]\ndatadir=C:/mysql/data\nlog-error=C:/logs/a.err\n");
            var basedir = MyIniParser.Parse("[mysqld]\ndatadir=D:/dados\n");
            var m = MyIniParser.Merge(new[] { windows, basedir });
            Assert.Equal(@"D:\dados", m.DataDir);          // o último prevalece
            Assert.Equal(@"C:\logs\a.err", m.LogError);   // só definido no primeiro: mantém
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
            var p = DbPathResolver.Resolve(null, DbPathResolver.BaseDirFromExe(@"C:\mysql\bin\mysqld.exe"), "LOJA");
            Assert.Equal(@"C:\mysql\data", p.DataDir);
            Assert.Equal(@"C:\mysql\data\LOJA.err", p.ErrPath);
            Assert.Equal(new[]
            {
                @"C:\WINDOWS\my.ini", @"C:\WINDOWS\my.cnf", @"C:\my.ini", @"C:\my.cnf",
                @"C:\mysql\my.ini", @"C:\mysql\my.cnf", @"C:\mysql\data\my.ini", @"C:\mysql\data\my.cnf",
            }, DbPathResolver.IniCandidates(@"C:\mysql", @"C:\WINDOWS"));
        }
    }
}
