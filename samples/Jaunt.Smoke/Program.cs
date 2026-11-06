using Jaunt;
using var e = new JsEngine(new JsEngineOptions { Limits = new JsLimits { Timeout = TimeSpan.FromSeconds(3) } });
try { e.Evaluate(File.ReadAllText(args[0])); } catch (Exception ex) { Console.WriteLine(ex.GetType().Name + ": " + ex.Message + "\n" + ex.InnerException); }
if (args.Length > 1) try { Console.WriteLine(e.Evaluate(File.ReadAllText(args[1]))); } catch (Exception ex) { Console.WriteLine(ex.GetType().Name + ": " + ex.Message); }
