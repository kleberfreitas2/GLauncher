using System.ClientModel;
using System.Net.Http;
using Microsoft.SemanticKernel;

namespace GameLauncher.Services;

public static class AiErrorMessages
{
    public static string Describe(Exception exception)
    {
        int? status = null;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("API key not valid", StringComparison.OrdinalIgnoreCase))
                return "A chave API foi recusada. Confira o provedor e a chave em Configurar assistente IA. Não é um limite de tokens.";

            status = current switch
            {
                ClientResultException client => client.Status,
                HttpOperationException operation when operation.StatusCode.HasValue => (int)operation.StatusCode.Value,
                HttpRequestException request when request.StatusCode.HasValue => (int)request.StatusCode.Value,
                _ => status
            };
        }

        return status switch
        {
            401 => "A chave API foi recusada. Confira o provedor e a chave em Configurar assistente IA. Não é um limite de tokens.",
            403 => "A conta não tem permissão para esta solicitação. Confira restrições da chave, disponibilidade regional e acesso ao modelo no provedor.",
            429 => "O provedor informou limite de uso ou cota esgotada. Aguarde e confira a cota/créditos no painel da sua conta. Isso não significa que a chave expirou. Nenhum outro provedor foi acionado automaticamente.",
            402 => "O provedor exige créditos ou configuração de cobrança. Confira o saldo no painel da sua conta.",
            404 => "O modelo ou endpoint não está disponível para esta conta. Verifique a disponibilidade no provedor ou selecione outro serviço nas configurações.",
            400 or 413 => "O provedor não aceitou a solicitação. Ela pode exceder o limite de contexto; tente uma mensagem menor ou abra uma nova conversa.",
            >= 500 => "O serviço de IA está temporariamente indisponível. Tente novamente mais tarde.",
            _ when exception is HttpRequestException => "Não foi possível conectar ao serviço de IA. Verifique sua conexão e tente novamente.",
            _ => "Não foi possível obter a resposta. Confira a conexão e as configurações do provedor e tente novamente."
        };
    }
}
