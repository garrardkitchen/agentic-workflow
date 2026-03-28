using Spectre.Console;

string name;

if (Console.IsInputRedirected)
{
    Console.Write("What's your name? ");
    name = Console.ReadLine() ?? string.Empty;
}
else
{
    name = AnsiConsole.Prompt(
        new TextPrompt<string>("What's your [green]name[/]?")
            .PromptStyle("cyan")
            .ValidationErrorMessage("[red]Please enter a valid name[/]")
            .Validate(input => !string.IsNullOrWhiteSpace(input)));
}

if (string.IsNullOrWhiteSpace(name))
{
    Console.Error.WriteLine("Please enter a valid name.");
    Environment.ExitCode = 1;
    return;
}

if (Console.IsOutputRedirected)
{
    Console.WriteLine($"Hello, {name}!");
}
else
{
    AnsiConsole.MarkupLine($"Hello, [bold yellow]{Markup.Escape(name)}[/]!");
}
