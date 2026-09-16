
// The experiment suite: command parsing and validation, argument conversion, target dispatch, execution
// semantics, value paths and the shipped command files. It links the plugin's own sources and never loads
// the game; the runner and the recorder are replaced by doubles.
var tests = new Suite();
ParserTests.Run(tests);
ConversionTests.Run(tests);
TargetTests.Run(tests);
EngineTests.Run(tests);
ValueTests.Run(tests);
ShippedCommandTests.Run(tests);
return tests.Report();

