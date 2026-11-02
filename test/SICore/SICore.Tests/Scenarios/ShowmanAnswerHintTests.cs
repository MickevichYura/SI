using NSubstitute;
using NUnit.Framework;
using SI.Contracts;
using SICore.Contracts;
using SICore.Network;
using SICore.Network.Clients;
using SICore.Network.Servers;
using SIData;
using SIPackages;
using SIPackages.Core;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SICore.Tests.Scenarios;

/// <summary>
/// Tests right answers sent to the showman as a hint.
/// </summary>
[TestFixture]
public sealed class ShowmanAnswerHintTests
{
    [Test]
    public async Task EmptyRightAnswer_AnswerContentTextIsSent()
    {
        var question = CreateQuestion();
        question.Right.Add("");

        question.Parameters[QuestionParameterNames.Answer] = new StepParameter
        {
            Type = StepParameterTypes.Content,
            ContentValue = new List<ContentItem>
            {
                new() { Type = ContentTypes.Image, Value = "answer.jpg", IsRef = true },
                new() { Type = ContentTypes.Text, Value = "1) Scream 3\n2) Scream 7\r\n\r\n3) Scream" },
            }
        };

        var args = await GetQuestionAnswersAsync(question);

        Assert.That(args, Is.EqualTo(new[] { Messages.QuestionAnswers, "1", "1) Scream 3; 2) Scream 7; 3) Scream" }));
    }

    [Test]
    public async Task MultilineAnswers_AreSentAsSingleArguments()
    {
        var question = CreateQuestion();
        question.Right.Add("Krampus\nKrampus (2015)");
        question.Wrong.Add("Pazuzu\nThe Exorcist");

        var args = await GetQuestionAnswersAsync(question);

        Assert.That(args, Is.EqualTo(new[] { Messages.QuestionAnswers, "1", "Krampus; Krampus (2015)", "Pazuzu; The Exorcist" }));
    }

    private static Question CreateQuestion()
    {
        var question = new Question { Price = 100, TypeName = QuestionTypes.Simple };

        question.Parameters[QuestionParameterNames.Question] = new StepParameter
        {
            Type = StepParameterTypes.Content,
            ContentValue = new List<ContentItem> { new() { Type = ContentTypes.Text, Value = "Question" } }
        };

        return question;
    }

    private static async Task<string[]> GetQuestionAnswersAsync(Question question)
    {
        var node = new PrimaryNode(new Network.Configuration.NodeConfiguration());

        var gameSettings = new GameSettingsCore<AppSettingsCore>
        {
            Showman = new Account { IsHuman = true, Name = Constants.FreePlace },
            Players =
            [
                new Account { IsHuman = true, Name = Constants.FreePlace },
                new Account { IsHuman = true, Name = Constants.FreePlace }
            ],
        };

        var document = SIDocument.Create("Package", "Author");
        var round = new Round { Name = "Round", Type = RoundTypes.Standart };
        document.Package.Rounds.Add(round);
        var theme = new Theme { Name = "Theme" };
        round.Themes.Add(theme);
        theme.Questions.Add(question);

        var game = GameRunner.CreateGame(
            node,
            gameSettings,
            new RoomSettings
            {
                HostName = "Showman",
                Showman = new SI.Contracts.Models.Account
                {
                    Type = SI.Contracts.Models.AccountType.Human,
                    Name = Constants.FreePlace
                },
                Players =
                [
                    new SI.Contracts.Models.Account { Type = SI.Contracts.Models.AccountType.Human, Name = Constants.FreePlace },
                    new SI.Contracts.Models.Account { Type = SI.Contracts.Models.AccountType.Human, Name = Constants.FreePlace }
                ]
            },
            new SI.Contracts.TimeSettings(),
            new RulesSettings(),
            "en-US",
            document,
            Substitute.For<IGameHost>(),
            Substitute.For<IFileShare>(),
            Array.Empty<ComputerAccount>(),
            Array.Empty<ComputerAccount>(),
            Array.Empty<ComputerAccount>(),
            Substitute.For<IAvatarHelper>(),
            null,
            null);

        game.Run();

        var showman = Join(node, game, "Showman", GameRole.Showman);
        Join(node, game, "A", GameRole.Player);
        Join(node, game, "B", GameRole.Player);

        showman.Client.SendMessage(Messages.Start);
        await showman.WaitForMessageAsync(Messages.AskSelectPlayer);

        showman.Client.SendMessage(new MessageBuilder(Messages.SelectPlayer, 0).ToString(), receiver: NetworkConstants.GameName);
        return await showman.WaitForMessageAsync(Messages.QuestionAnswers);
    }

    private static Listener Join(Node node, Game game, string name, GameRole role)
    {
        var client = new Client(name);
        var listener = new Listener(client);
        client.ConnectTo(node);
        Assert.That(game.Authenticate(name, false, role, null), Is.EqualTo(AuthenticationResult.Ok));
        return listener;
    }

    private sealed class Listener
    {
        private readonly Channel<Message> _messageChannel = Channel.CreateUnbounded<Message>();

        public Client Client { get; }

        public Listener(Client client)
        {
            Client = client;
            Client.MessageReceived += message => _messageChannel.Writer.WriteAsync(message);
        }

        public async Task<string[]> WaitForMessageAsync(string messageType)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            while (await _messageChannel.Reader.WaitToReadAsync(cts.Token))
            {
                while (_messageChannel.Reader.TryRead(out var message))
                {
                    var args = message.Text.Split(Message.ArgsSeparatorChar);

                    if (args[0] == messageType)
                    {
                        return args;
                    }
                }
            }

            throw new InvalidOperationException($"Timeout waiting for {messageType}");
        }
    }
}
