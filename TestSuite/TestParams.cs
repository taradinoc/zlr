using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ZLR.VM;

namespace TestSuite
{
    class TestParams
    {
        public static readonly TestParams Default = new TestParams();

        private enum IoType
        {
            Default,
            Grid,
        }

        private IoType ioType = IoType.Default;
        // Grid
        private int ioCols, ioRows;

        private TestParams()
        {
        }

        private static readonly Regex commentRE = new Regex(@"^\s*(?:#.*)$");
        private static readonly Regex gridIoRE = new Regex(@"^gridio\s+(\d+)\s+(\d+)", RegexOptions.IgnoreCase);

        public TestParams(string paramsFile)
        {
            foreach (var line in File.ReadAllLines(paramsFile))
            {
                if (commentRE.IsMatch(line))
                    continue;

                Match match;

                if ((match = gridIoRE.Match(line)).Success)
                {
                    ioType = IoType.Grid;
                    ioCols = int.Parse(match.Groups[1].Value);
                    ioRows = int.Parse(match.Groups[2].Value);
                    continue;
                }
            }
        }

        public ITestCaseIO GetIO(string filename, bool recording)
        {
            switch (ioType)
            {
                case IoType.Default:
                    return recording ? (ITestCaseIO)new RecordingIO(filename) : new ReplayIO(filename);

                case IoType.Grid:
                    Console.SetWindowSize(ioCols, ioRows);
                    return recording ? (ITestCaseIO)new RecordingGridIO(filename) : new ReplayGridIO(filename);

                default:
                    throw new NotImplementedException();
            }
        }
    }
}
