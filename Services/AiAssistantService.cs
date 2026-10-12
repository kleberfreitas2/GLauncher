using System.Runtime.CompilerServices;
using GameLauncher.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace GameLauncher.Services;

public enum AiProvider { Groq, OpenAI, Gemini }

/// <summary>
/// Assistente IA gamer usando Semantic Kernel.
/// Suporta Groq, OpenAI e Gemini. O modelo Groq configurado usa apenas texto.
/// </summary>
public class AiAssistantService : IDisposable
{
    private readonly IChatCompletionService _chat;
    private readonly ChatHistory _history;
    private readonly AiProvider _provider;

    // Endpoint compatível com OpenAI usado pelo Groq
    private static readonly Uri GroqEndpoint = new("https://api.groq.com/openai/v1");
    private static readonly Uri GeminiEndpoint = new("https://generativelanguage.googleapis.com/v1beta/openai/");

    public bool SupportsImages => _provider != AiProvider.Groq;

    public AiAssistantService(AiProvider provider, string apiKey, string? currentGameName = null)
    {
        _provider = provider;

        var builder = Kernel.CreateBuilder();

        if (provider == AiProvider.Groq)
        {
            // Groq é compatível com a API OpenAI.
            // Modelo OpenAI open-weight disponível no catálogo atual da Groq.
            builder.AddOpenAIChatCompletion(
                modelId: "openai/gpt-oss-20b",
                apiKey: apiKey,
                endpoint: GroqEndpoint);
        }
        else if (provider == AiProvider.Gemini)
        {
            builder.AddOpenAIChatCompletion(
                modelId: "gemini-2.5-flash",
                apiKey: apiKey,
                endpoint: GeminiEndpoint);
        }
        else
        {
            builder.AddOpenAIChatCompletion("gpt-4o", apiKey);
        }

        var kernel = builder.Build();
        _chat = kernel.GetRequiredService<IChatCompletionService>();
        _history = new ChatHistory();
        _history.AddSystemMessage(BuildSystemPrompt(currentGameName));
    }

    /// <summary>
    /// Envia uma mensagem (com screenshot opcional) e retorna a resposta em streaming.
    /// Análise de screenshot disponível com OpenAI e Gemini.
    /// </summary>
    public async IAsyncEnumerable<string> SendAsync(
        string userMessage,
        string? imageBase64 = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var historyCount = _history.Count;
        if (imageBase64 is not null && SupportsImages)
        {
            var imageBytes = Convert.FromBase64String(imageBase64);
            var content = new ChatMessageContentItemCollection
            {
                new TextContent(userMessage),
                new ImageContent(imageBytes, "image/png")
            };
            _history.AddUserMessage(content);
        }
        else
        {
            if (imageBase64 is not null)
                _history.AddUserMessage($"[Screenshot não analisado: o modelo Groq configurado aceita apenas texto.]\n{userMessage}");
            else
                _history.AddUserMessage(userMessage);
        }

        var fullResponse = new System.Text.StringBuilder();

        var completed = false;
        try
        {
            await foreach (var chunk in _chat.GetStreamingChatMessageContentsAsync(
                _history, cancellationToken: cancellationToken))
            {
                var token = chunk.Content ?? string.Empty;
                if (!string.IsNullOrEmpty(token))
                {
                    fullResponse.Append(token);
                    yield return token;
                }
            }
            _history.AddAssistantMessage(fullResponse.ToString());
            completed = true;
        }
        finally
        {
            if (!completed)
                while (_history.Count > historyCount)
                    _history.RemoveAt(_history.Count - 1);
        }
    }

    private static string BuildSystemPrompt(string? gameName)
    {
        var gameContext = gameName is not null
            ? $"O jogo de referência é **{gameName}**. Isso não informa a fase atual do jogador."
            : "Nenhum jogo foi informado. Pergunte qual jogo o usuário quer discutir.";

        return $"""
            Você é o **GLauncher AI**, um assistente especialista em jogos integrado ao GLauncher.
            {gameContext}

            Suas diretrizes:
            - Responda sempre em português do Brasil, de forma amigável e entusiasmada.
            - Dê dicas táticas, estratégicas e de progressão sem revelar spoilers importantes, a menos que o usuário peça explicitamente.
            - Se o usuário enviar um screenshot, analise a situação do jogo e ofereça conselhos contextuais.
            - Seja conciso mas útil. Prefira listas quando listar dicas.
            - Se não souber algo sobre um jogo específico, admita honestamente.
            - Para ajudar a passar de fase, pergunte a missão, região, chefe ou puzzle quando esse contexto faltar.
            - Quando houver contexto suficiente, organize a ajuda em objetivo, preparação, passos numerados e estratégia alternativa.
            - Não invente itens, caminhos, mecânicas nem fontes. Você não possui busca na web nesta conversa.
            - Mantenha o histórico da conversa para dar respostas contextuais.
            """;
    }

    public void Dispose() { }
}
