using System.Reflection;
using Simple.Arena;

namespace MongoSpyglass.Proxy.WireProtocol;

internal class MessageParserRegistry
{
    private readonly Dictionary<Type, IMessageParser> _messageParsers = new();

    public MessageParserRegistry()
    {
        // Scan the current assembly for types that implement IMessageParser<T> and instantiate them
        var thisAssembly = Assembly.GetExecutingAssembly();
        foreach (var type in thisAssembly.GetTypes().Where(t => t.IsClass))
        {
            foreach (var @interface in type.GetInterfaces())
            {
                if (@interface.IsGenericType && 
                    @interface.GetGenericTypeDefinition() == typeof(IMessageParser<>))
                {
                    var genericArg = @interface.GetGenericArguments()[0];
                    if (!_messageParsers.ContainsKey(genericArg))
                    {
                        _messageParsers[genericArg] = (IMessageParser)Activator.CreateInstance(type)! ?? throw new InvalidOperationException();
                    }
                }
            }
        }
    }

    public bool TryGet<TMessage>(out IMessageParser<TMessage> parser) where TMessage : unmanaged
    {
        if (_messageParsers.TryGetValue(typeof(TMessage), out var untypedParser))
        {
            parser = (IMessageParser<TMessage>)untypedParser;
            return true;
        }
        parser = default;
        return false;
    }
}