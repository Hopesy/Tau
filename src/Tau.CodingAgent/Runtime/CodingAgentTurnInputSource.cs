using System.Runtime.CompilerServices;

namespace Tau.CodingAgent.Runtime;

public enum CodingAgentTurnInputKind
{
    Steering,
    FollowUp
}

public sealed record CodingAgentTurnInput(CodingAgentTurnInputKind Kind, string Text);

public interface ICodingAgentTurnInputSource
{
    IAsyncEnumerable<CodingAgentTurnInput> ReadInputsAsync(CancellationToken cancellationToken = default);
}

public sealed class SystemConsoleCodingAgentTurnInputSource : ICodingAgentTurnInputSource
{
    private const int PollDelayMilliseconds = 25;

    public async IAsyncEnumerable<CodingAgentTurnInput> ReadInputsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new List<char>();

        while (!cancellationToken.IsCancellationRequested)
        {
            bool hasKey;
            try
            {
                hasKey = Console.KeyAvailable;
            }
            catch (InvalidOperationException)
            {
                yield break;
            }
            catch (IOException)
            {
                yield break;
            }

            if (!hasKey)
            {
                try
                {
                    await Task.Delay(PollDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                continue;
            }

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                var text = new string(buffer.ToArray()).Trim();
                buffer.Clear();
                if (text.Length == 0)
                {
                    continue;
                }

                var kind = (key.Modifiers & ConsoleModifiers.Alt) != 0
                    ? CodingAgentTurnInputKind.FollowUp
                    : CodingAgentTurnInputKind.Steering;
                yield return new CodingAgentTurnInput(kind, text);
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Count > 0)
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                continue;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                buffer.Clear();
                continue;
            }

            if (key.KeyChar != '\0' &&
                !char.IsControl(key.KeyChar) &&
                (key.Modifiers & ConsoleModifiers.Control) == 0)
            {
                buffer.Add(key.KeyChar);
            }
        }
    }
}
