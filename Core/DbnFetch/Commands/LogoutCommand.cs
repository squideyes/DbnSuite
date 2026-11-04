using System.CommandLine;

namespace DbnFetch.Commands;

/// <summary><c>dbnf logout</c>: forgets a saved login and its secret.</summary>
public static class LogoutCommand
{
    public static Command Create(Session session)
    {
        var command = new Command("logout", "Forget a saved login and its secret key.");

        command.SetAction(async (parse, cancel) =>
        {
            var name = parse.GetValue(session.Profile)!;

            var removed = session.Profiles.Remove(name);

            if (!removed.IsSuccess)
                return await session.FailAsync(removed.Error!).ConfigureAwait(false);

            await session.Out.WriteLineAsync(removed.Value
                ? $"Forgot the {name} login."
                : $"There was no {name} login.").ConfigureAwait(false);

            return Session.Ok;
        });

        return command;
    }
}
