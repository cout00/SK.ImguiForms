using SK.ImguiForms;

namespace SK.ImguiForms.Example;

static class Program
{
    [STAThread]
    static void Main()
    { 
        ImguiApplication.Start(new ExampleForm()).GetAwaiter().GetResult();
    }
}
