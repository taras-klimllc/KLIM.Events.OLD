FROM rabbitmq:4.1.3-management

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fSL -o /opt/rabbitmq/plugins/rabbitmq_delayed_message_exchange-4.1.0.ez \
    https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/releases/download/v4.1.0/rabbitmq_delayed_message_exchange-4.1.0.ez \
    && chown rabbitmq:rabbitmq /opt/rabbitmq/plugins/rabbitmq_delayed_message_exchange-4.1.0.ez \
    && chmod 644 /opt/rabbitmq/plugins/rabbitmq_delayed_message_exchange-4.1.0.ez \
    && chmod 755 /opt/rabbitmq/plugins

RUN rabbitmq-plugins enable --offline \
    rabbitmq_management \
    rabbitmq_prometheus \
    rabbitmq_consistent_hash_exchange \
    rabbitmq_shovel \
    rabbitmq_shovel_management \
    rabbitmq_delayed_message_exchange
