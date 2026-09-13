using ForgeRuntime;
int count = 0;
bool Near(float a, float b) => Math.Abs(a - b) < 0.0001f;
void Check(bool ok, string message) { count++; if (!ok) throw new Exception(message); }
var performanceSamples = new PerformanceSampleWindow(128);
for (var milliseconds = 1; milliseconds <= 100; milliseconds++) performanceSamples.Add(milliseconds, 20);
Check(performanceSamples.TryTake(out var frameSummary), "A populated performance window must produce a summary.");
Check(frameSummary.Samples == 100 && frameSummary.SlowFrames == 81 && Near((float)frameSummary.AverageMilliseconds, 50.5f), "Frame summaries must retain sample, threshold and average values.");
Check(frameSummary.P50Milliseconds == 50 && frameSummary.P95Milliseconds == 95 && frameSummary.P99Milliseconds == 99 && frameSummary.MaximumMilliseconds == 100, "Frame summary percentiles must use nearest-rank ordering.");
Check(!performanceSamples.TryTake(out _), "Taking a performance summary must reset its interval.");

var overflowWindow = new PerformanceSampleWindow(2);
overflowWindow.Add(10, 20); overflowWindow.Add(20, 20); overflowWindow.Add(500, 20); overflowWindow.Add(1, 20);
Check(overflowWindow.TryTake(out var overflow) && overflow.Samples == 4 && overflow.DroppedSamples == 2, "Overflow must report every observed frame and percentile truncation.");
Check(overflow.MinimumMilliseconds == 1 && overflow.MaximumMilliseconds == 500 && overflow.AverageMilliseconds == 132.75, "Dropped percentile samples must still contribute to true extrema and mean.");
overflowWindow.Add(7, 20);
Check(overflowWindow.TryTake(out var resetWindow) && resetWindow.MinimumMilliseconds == 7 && resetWindow.MaximumMilliseconds == 7, "Extrema must reset between intervals.");
Console.WriteLine($"PASS: {count} frame-sampling checks.");
