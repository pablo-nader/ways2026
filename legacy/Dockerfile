# Fase 0 — continuidad operativa.
#
# Esta imagen empaqueta el sistema PHP original tal cual, sin reescribir nada. Lo único que
# cambió respecto del hosting viejo son las credenciales, que ahora entran por entorno.
#
# La base es php:7.4-apache porque el hosting original corría PHP 7.4.33 y en Fase 0 la
# prioridad es reproducir, no modernizar. Está fuera de soporte, y no importa: esto vive
# hasta el cutover. Para probar con PHP 8 sin tocar el Dockerfile:
#
#   docker build --build-arg PHP_IMAGE=php:8.2-apache -t ways-legacy .
ARG PHP_IMAGE=php:7.4-apache
FROM ${PHP_IMAGE}

# El sistema habla con MySQL por mysqli; no viene compilada en la imagen oficial.
RUN docker-php-ext-install mysqli \
    && a2enmod remoteip \
    && rm -rf /var/lib/apt/lists/*

COPY config/php-ways.ini /usr/local/etc/php/conf.d/zz-ways.ini
COPY config/apache-ways.conf /etc/apache2/conf-available/ways.conf
RUN a2enconf ways

# Las sesiones viven fuera de /tmp para poder montarlas en un volumen: si se pierden, todos
# los cajeros quedan deslogueados en el medio del turno.
RUN mkdir -p /var/lib/php/sessions \
    && chown www-data:www-data /var/lib/php/sessions \
    && chmod 700 /var/lib/php/sessions

COPY app/ /var/www/html/
RUN chown -R www-data:www-data /var/www/html

EXPOSE 80
