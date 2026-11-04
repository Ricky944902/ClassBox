using System.Reflection;

namespace ClassBox.Services;

/// <summary>
/// 极简依赖注入容器（构造函数注入 + 单例/瞬时）。
/// 刻意不引入第三方 DI 框架，保持"零 NuGet 依赖"。
/// </summary>
public sealed class ServiceContainer
{
    private readonly Dictionary<Type, Func<ServiceContainer, object>> _factories = new();
    private readonly Dictionary<Type, object> _instances = new();
    private readonly HashSet<Type> _creating = new();

    public void RegisterInstance<T>(T instance) where T : class
        => _instances[typeof(T)] = instance;

    public void RegisterSingleton<TService, TImplementation>()
        where TImplementation : class, TService
        => _factories[typeof(TService)] = container => container.GetOrCreateSingleton(typeof(TImplementation), typeof(TService));

    public void RegisterSingleton<TService>(Func<ServiceContainer, TService> factory) where TService : class
        => _factories[typeof(TService)] = container => factory(container);

    public T Resolve<T>() where T : class => (T)Resolve(typeof(T));

    public object Resolve(Type type)
    {
        if (_instances.TryGetValue(type, out var instance)) return instance;

        if (_factories.TryGetValue(type, out var factory))
        {
            return factory(this);
        }

        return CreateWithConstructorInjection(type);
    }

    private object GetOrCreateSingleton(Type implementation, Type serviceType)
    {
        if (_instances.TryGetValue(implementation, out var existing)) return existing;

        var created = CreateWithConstructorInjection(implementation);
        _instances[implementation] = created;
        _instances[serviceType] = created;
        return created;
    }

    private object CreateWithConstructorInjection(Type type)
    {
        if (!_creating.Add(type))
        {
            throw new InvalidOperationException($"检测到循环依赖：{type.FullName}");
        }

        try
        {
            var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (constructor is null)
            {
                throw new InvalidOperationException($"{type.FullName} 没有可用的公共构造函数");
            }

            var parameters = constructor.GetParameters()
                .Select(p => Resolve(p.ParameterType))
                .ToArray();

            var instance = constructor.Invoke(parameters);

            if (!_instances.ContainsKey(type))
            {
                _instances[type] = instance;
            }

            return instance;
        }
        finally
        {
            _creating.Remove(type);
        }
    }
}
